using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>
/// tam.documents (docs/35 arc 5): the tenant's document tree — folders as a materialized
/// path-tree, content-addressed file storage behind the <see cref="IDocumentStore"/> seam,
/// documents attached to records by EntityRef, and folder ACLs as stored ReachRef strings
/// evaluated through <see cref="ReachResolver"/> on read AND write — the first consumer of
/// the reach seam, and the docs/28 one-predicate discipline applied to visibility.
/// </summary>
[TamPackage("tam.documents", "documents", "web.documents")]
public sealed class TamDocumentsPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        // Nav CONTENT + suggestion (docs/30 D-N2) — the host owns placement; the ERP host
        // will surface a richer browser when the FE tree lands.
        plugin.Nav(nav => nav
            .Page("documents.list", grid: "web.documents.list", suggest: "administration", order: 60)
            .Page("documents.folders", grid: "web.documents.folders", suggest: "administration", order: 61));
        plugin
            .AddOperationType(typeof(DefineFolder))
            .AddOperationType(typeof(ShareFolder))
            .AddOperationType(typeof(UnshareFolder))
            .AddOperationType(typeof(UploadDocument))
            .AddOperationType(typeof(RetireDocument))
            .AddViewType(typeof(FolderList))
            .AddViewType(typeof(DocumentList))
            .Form<DefineFolder.Input>("web.documents.folders.define", "documents.folders.define")
            .Form<ShareFolder.Input>("web.documents.folders.share", "documents.folders.share", form =>
            {
                form.Field(x => x.FolderId).Renderer("hidden");
                form.Field(x => x.Reach);
            })
            .Form<UnshareFolder.Input>("web.documents.folders.unshare", "documents.folders.unshare", form =>
            {
                form.Field(x => x.FolderId).Renderer("hidden");
                form.Field(x => x.Reach);
            })
            .Form<UploadDocument.Input>("web.documents.upload", "documents.upload", form =>
            {
                form.Field(x => x.FolderId).Renderer("hidden");
                form.Field(x => x.FileName);
                form.Field(x => x.ContentBase64).Renderer("file");
                form.Field(x => x.ContentType).Renderer("hidden");
                form.Field(x => x.AttachedTo).Renderer("hidden");
            })
            .Grid<FolderList.Result>("web.documents.folders", "documents.folders.list", grid =>
            {
                grid.RowForm("documents.folders.share");
                grid.RowForm("documents.folders.unshare");
                grid.ToolbarAction("documents.folders.define");
            })
            .Grid<DocumentList.Result>("web.documents.list", "documents.list", grid =>
            {
                grid.RowAction("documents.retire");
            });

        // Magic folders (docs/35): ONE wildcard subscriber serves every host-declared
        // DocumentFolder binding — which events materialize folders is MODEL data, so the
        // package never registers per event (the wildcard-gate precedent, docs/28).
        plugin.OnEffect<MagicFolderSubscriber>("*");
    }
}

public static class DocumentFindings
{
    public static readonly FindingFactory InvalidPath = Finding.Error("documents.invalid-path");
    public static readonly FindingFactory UnknownFolder = Finding.Error("documents.unknown-folder");
    public static readonly FindingFactory UnknownReach = Finding.Error("documents.unknown-reach");
    public static readonly FindingFactory ShareNotFound = Finding.Error("documents.share-not-found");
    public static readonly FindingFactory NoAccess = Finding.Error("documents.no-access");
    public static readonly FindingFactory NotFound = Finding.Error("documents.not-found");
    public static readonly FindingFactory InvalidContent = Finding.Error("documents.invalid-content");
    public static readonly FindingFactory TooLarge = Finding.Error("documents.too-large");
    public static readonly FindingFactory UnknownEntity = Finding.Error("documents.unknown-entity");
}

/// <summary>
/// File CONTENT storage behind a seam: content-addressed by SHA-256, so a deployment swaps in
/// blob storage (S3, Azure) without touching metadata or ACLs. The default stores blobs in
/// the database, tenant-scoped — covered by the ambient filter and the RLS backstop exactly
/// like metadata. Writes ride the operation's transaction (no SaveChanges here).
/// </summary>
public interface IDocumentStore
{
    Task<string> PutAsync(byte[] content, CancellationToken ct);

    Task<Stream?> OpenAsync(string hash, CancellationToken ct);
}

public sealed class DbDocumentStore(ITamDb tam) : IDocumentStore
{
    public async Task<string> PutAsync(byte[] content, CancellationToken ct)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        // Content-addressed: identical content stores once per tenant (the ambient filter
        // scopes the existence probe).
        if (!await tam.Db.Set<DocumentBlobEntity>().AnyAsync(b => b.Hash == hash, ct))
            tam.Db.Add(new DocumentBlobEntity { Id = Guid.NewGuid(), Hash = hash, Content = content });
        return hash;
    }

    public async Task<Stream?> OpenAsync(string hash, CancellationToken ct)
    {
        var blob = await tam.Db.Set<DocumentBlobEntity>()
            .Where(b => b.Hash == hash).Select(b => b.Content).SingleOrDefaultAsync(ct);
        return blob is null ? null : new MemoryStream(blob, writable: false);
    }
}

/// <summary>
/// THE visibility predicate (docs/28 one-predicate discipline): a folder is visible when its
/// EFFECTIVE ACL — its own reach rows, else the nearest ancestor's, else unrestricted —
/// contains the acting actor. Views filter reads through it and every write re-checks it;
/// documents.manage holders administer the whole tree. Reach containment is memoized per
/// distinct ref: tens of folders and a handful of refs, not thousands (the docs/24 profile).
/// </summary>
public static class DocumentAccess
{
    public static async Task<List<Guid>> VisibleFolderIdsAsync(
        ITamDb tam, ReachResolver reach, OperationContext context, CancellationToken ct)
    {
        var folders = await tam.Db.Set<FolderEntity>().ToListAsync(ct);
        if (context.Actor.Can("documents.manage"))
            return folders.Select(f => f.Id).ToList();

        var acls = (await tam.Db.Set<DocumentAclEntity>().ToListAsync(ct))
            .ToLookup(a => a.FolderId);
        var byPath = folders.ToDictionary(f => f.Path);
        var within = new Dictionary<string, bool>();
        var visible = new List<Guid>();
        foreach (var folder in folders)
        {
            var effective = EffectiveAcl(folder, byPath, acls);
            if (effective is null) { visible.Add(folder.Id); continue; }
            foreach (var entry in effective)
            {
                if (!within.TryGetValue(entry.Reach, out var contained))
                    within[entry.Reach] = contained = await reach.WithinAsync(entry.Reach, context, ct);
                if (contained) { visible.Add(folder.Id); break; }
            }
        }
        return visible;
    }

    private static IReadOnlyList<DocumentAclEntity>? EffectiveAcl(
        FolderEntity folder, Dictionary<string, FolderEntity> byPath,
        ILookup<Guid, DocumentAclEntity> acls)
    {
        for (var probe = folder; probe is not null; probe = Parent(probe, byPath))
        {
            var own = acls[probe.Id].ToList();
            if (own.Count > 0) return own;
        }
        return null;
    }

    private static FolderEntity? Parent(FolderEntity folder, Dictionary<string, FolderEntity> byPath)
    {
        var cut = folder.Path.LastIndexOf('/');
        return cut <= 0 ? null : byPath.GetValueOrDefault(folder.Path[..cut]);
    }

    /// <summary>mkdir -p, shared by the define intent and the magic-folder subscriber:
    /// creates every missing folder along the path, un-retires ones that exist, and returns
    /// the leaf — or null for an invalid path. Idempotent (at-least-once delivery safe).</summary>
    public static async Task<FolderEntity?> EnsureFoldersAsync(
        ITamDb tam, string path, CancellationToken ct)
    {
        var prefixes = NormalizedPrefixes(path);
        if (prefixes is null) return null;
        var existing = await tam.Db.Set<FolderEntity>()
            .Where(f => prefixes.Contains(f.Path)).ToDictionaryAsync(f => f.Path, ct);
        FolderEntity leaf = null!;
        foreach (var prefix in prefixes)
        {
            if (existing.TryGetValue(prefix, out var found))
            {
                found.Retired = false;
                leaf = found;
                continue;
            }
            leaf = new FolderEntity
            {
                Id = Guid.NewGuid(),
                Path = prefix,
                Name = prefix[(prefix.LastIndexOf('/') + 1)..],
            };
            tam.Db.Add(leaf);
        }
        return leaf;
    }

    /// <summary>"/avtal/2026" → ["/avtal", "/avtal/2026"]; null for an empty or overlong path.
    /// Segments are free text minus '/' — human names, not slugs.</summary>
    public static IReadOnlyList<string>? NormalizedPrefixes(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments.Any(s => s.Length > 200)) return null;
        var prefixes = new List<string>(segments.Length);
        var current = "";
        foreach (var segment in segments)
        {
            current = $"{current}/{segment}";
            prefixes.Add(current);
        }
        return prefixes.Last().Length > 1000 ? null : prefixes;
    }
}

/// <summary>mkdir -p as an intent: creates every missing folder along the path; a retired
/// folder under the same path un-retires (the roles.define idiom).</summary>
[Operation("documents.folders.define")]
[Authorize("documents.manage")]
public static class DefineFolder
{
    public sealed record Input([property: LabelKey("labels.path")] string Path);

    public sealed record Output(Guid FolderId, string Path);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var leaf = await DocumentAccess.EnsureFoldersAsync(tam, input.Path, ct);
        if (leaf is null) return DocumentFindings.InvalidPath.At(nameof(Input.Path));
        return new Output(leaf.Id, leaf.Path);
    }
}

/// <summary>Grants a reach on a folder (docs/35): the ref must parse AND name a registered
/// kind — a teaching finding, not silent acceptance of a typo that would lock the folder.
/// Idempotent: an existing identical grant is a no-op.</summary>
[Operation("documents.folders.share")]
[Authorize("documents.manage")]
public static class ShareFolder
{
    public sealed record Input(
        [property: LabelKey("labels.folder")] Guid FolderId,
        [property: LabelKey("labels.reach")] string Reach);

    public sealed record Output(Guid FolderId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model, CancellationToken ct)
    {
        var folder = await tam.Db.Set<FolderEntity>()
            .SingleOrDefaultAsync(f => f.Id == input.FolderId, ct);
        if (folder is null) return DocumentFindings.UnknownFolder;

        if (!ReachRef.TryParse(input.Reach, out var reach)
            || !model.Reaches.ContainsKey(reach.Kind))
            return DocumentFindings.UnknownReach
                .With(("reach", input.Reach)).At(nameof(Input.Reach));

        var canonical = reach.ToString();
        if (!await tam.Db.Set<DocumentAclEntity>()
                .AnyAsync(a => a.FolderId == folder.Id && a.Reach == canonical, ct))
            tam.Db.Add(new DocumentAclEntity
            {
                Id = Guid.NewGuid(),
                FolderId = folder.Id,
                Reach = canonical,
            });
        return new Output(folder.Id);
    }
}

[Operation("documents.folders.unshare")]
[Authorize("documents.manage")]
public static class UnshareFolder
{
    public sealed record Input(
        [property: LabelKey("labels.folder")] Guid FolderId,
        [property: LabelKey("labels.reach")] string Reach);

    public sealed record Output(Guid FolderId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var entry = await tam.Db.Set<DocumentAclEntity>()
            .SingleOrDefaultAsync(a => a.FolderId == input.FolderId && a.Reach == input.Reach, ct);
        if (entry is null)
            return DocumentFindings.ShareNotFound.At(nameof(Input.Reach));
        tam.Db.Remove(entry);
        return new Output(input.FolderId);
    }
}

/// <summary>
/// Upload rides the STANDARD pipeline — idempotency, audit, gates, findings — with content as
/// base64 in the input (bounded; a streaming path is a deliberate deferral). The write
/// re-checks the SAME visibility predicate the views filter by, and an attachment must name a
/// wire-known entity (fail closed, the docs/15 extension-targeting rule).
/// </summary>
[Operation("documents.upload")]
[Authorize("documents.add")]
public static class UploadDocument
{
    public const int MaxBytes = 5_000_000;

    public sealed record Input(
        [property: LabelKey("labels.folder")] Guid FolderId,
        [property: LabelKey("labels.file-name")] string FileName,
        [property: LabelKey("labels.file")] string ContentBase64,
        [property: LabelKey("labels.content-type")] string? ContentType = null,
        [property: LabelKey("labels.attached-to")] string? AttachedTo = null);

    public sealed record Output(Guid DocumentId, long Size);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model,
        ReachResolver reach, IDocumentStore store, CancellationToken ct)
    {
        var folder = await tam.Db.Set<FolderEntity>()
            .SingleOrDefaultAsync(f => f.Id == input.FolderId && !f.Retired, ct);
        if (folder is null) return DocumentFindings.UnknownFolder;

        var visible = await DocumentAccess.VisibleFolderIdsAsync(tam, reach, context, ct);
        if (!visible.Contains(folder.Id)) return DocumentFindings.NoAccess;

        byte[] content;
        try { content = Convert.FromBase64String(input.ContentBase64); }
        catch (FormatException)
        {
            return DocumentFindings.InvalidContent.At(nameof(Input.ContentBase64));
        }
        if (content.Length is 0 or > MaxBytes)
            return DocumentFindings.TooLarge.At(nameof(Input.ContentBase64));

        string? attachedTo = null;
        if (!string.IsNullOrWhiteSpace(input.AttachedTo))
        {
            if (!EntityRef.TryParse(input.AttachedTo, out var reference)
                || !model.ExtensibleEntityKeys.Contains(reference.EntityKey))
                return DocumentFindings.UnknownEntity
                    .With(("entity", input.AttachedTo!)).At(nameof(Input.AttachedTo));
            attachedTo = reference.ToString();
        }

        var document = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            FolderId = folder.Id,
            FileName = input.FileName,
            ContentType = string.IsNullOrWhiteSpace(input.ContentType)
                ? "application/octet-stream" : input.ContentType!,
            Size = content.Length,
            ContentHash = await store.PutAsync(content, ct),
            AttachedTo = attachedTo,
            UploadedByActorId = context.Actor.Id,
            UploadedByName = context.Actor.Name,
            UploadedAtIso = IsoTime.Now(),
        };
        tam.Db.Add(document);
        return new Output(document.Id, document.Size);
    }
}

/// <summary>Retire-don't-drop: the file drops out of listings; content and audit referents
/// stay.</summary>
[Operation("documents.retire")]
[Authorize("documents.manage")]
public static class RetireDocument
{
    public sealed record Input([property: LabelKey("labels.document")] Guid DocumentId);

    public sealed record Output(Guid DocumentId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var document = await tam.Db.Set<DocumentEntity>()
            .SingleOrDefaultAsync(d => d.Id == input.DocumentId, ct);
        if (document is null) return DocumentFindings.NotFound;
        document.Retired = true;
        return new Output(document.Id);
    }
}

/// <summary>The folder tree, ACL-filtered through the ONE predicate — an async view (the
/// executor awaits Execute) because reach containment resolves before the query shapes.</summary>
[View("documents.folders.list")]
[Authorize("documents.read")]
public static class FolderList
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("labels.path")]
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        [LabelKey("labels.shared")]
        public bool Shared { get; init; }
    }

    public static async Task<IQueryable<Result>> Execute(
        Query query, ITamDb tam, ReachResolver reach, OperationContext context, CancellationToken ct)
    {
        var visible = await DocumentAccess.VisibleFolderIdsAsync(tam, reach, context, ct);
        var folders = tam.Db.Set<FolderEntity>()
            .Where(f => !f.Retired && visible.Contains(f.Id));
        if (!string.IsNullOrWhiteSpace(query.Search))
            folders = folders.Where(f => f.Path.Contains(query.Search!));
        var acls = tam.Db.Set<DocumentAclEntity>();
        return folders.Select(f => new Result
        {
            Id = f.Id,
            Path = f.Path,
            Name = f.Name,
            Shared = acls.Any(a => a.FolderId == f.Id),
        });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.Path), nameof(Result.Name))
        .DefaultSort(nameof(Result.Path));
}

/// <summary>Documents in visible folders; <c>attachedTo</c> is the record-tab query (every
/// document attached to one EntityRef), <c>folderId</c> the browser's.</summary>
[View("documents.list")]
[Authorize("documents.read")]
public static class DocumentList
{
    public sealed record Query(
        [property: LabelKey("labels.folder")] Guid? FolderId = null,
        [property: LabelKey("labels.attached-to")] string? AttachedTo = null,
        string? Search = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("labels.file-name")]
        public string FileName { get; init; } = "";
        [LabelKey("labels.folder")]
        public string FolderPath { get; init; } = "";
        [LabelKey("labels.content-type")]
        public string ContentType { get; init; } = "";
        [LabelKey("labels.size")]
        public long Size { get; init; }
        [LabelKey("labels.attached-to")]
        public string? AttachedTo { get; init; }
        [LabelKey("labels.uploaded-by")]
        public string UploadedByName { get; init; } = "";
        [LabelKey("labels.uploaded-at")]
        public string UploadedAtIso { get; init; } = "";
    }

    public static async Task<IQueryable<Result>> Execute(
        Query query, ITamDb tam, ReachResolver reach, OperationContext context, CancellationToken ct)
    {
        var visible = await DocumentAccess.VisibleFolderIdsAsync(tam, reach, context, ct);
        var documents = tam.Db.Set<DocumentEntity>()
            .Where(d => !d.Retired && visible.Contains(d.FolderId));
        if (query.FolderId is { } folderId)
            documents = documents.Where(d => d.FolderId == folderId);
        if (!string.IsNullOrWhiteSpace(query.AttachedTo))
            documents = documents.Where(d => d.AttachedTo == query.AttachedTo);
        if (!string.IsNullOrWhiteSpace(query.Search))
            documents = documents.Where(d => d.FileName.Contains(query.Search!));

        return documents.Join(tam.Db.Set<FolderEntity>(),
            d => d.FolderId, f => f.Id, (d, f) => new Result
            {
                Id = d.Id,
                FileName = d.FileName,
                FolderPath = f.Path,
                ContentType = d.ContentType,
                Size = d.Size,
                AttachedTo = d.AttachedTo,
                UploadedByName = d.UploadedByName,
                UploadedAtIso = d.UploadedAtIso,
            });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.FileName), nameof(Result.UploadedAtIso))
        .Filterable(nameof(Result.ContentType))
        .DefaultSort(nameof(Result.UploadedAtIso), descending: true);
}

/// <summary>
/// The magic-folder materializer (docs/35): on ANY committed event, renders each matching
/// host-declared binding's template from the payload — "/order/{number}" → "/order/2026-01412"
/// — and ensures the folder exists. Skips a binding whose placeholder value is absent (the
/// contract is DOC001-checked, but payloads are data). Idempotent under at-least-once
/// delivery; runs in the record's tenant-pinned scope like every subscriber.
/// </summary>
internal sealed class MagicFolderSubscriber(ITamDb tam, TamModel model) : IEffectHandler
{
    public async Task HandleAsync(EffectEvent effect, CancellationToken ct)
    {
        foreach (var binding in model.DocumentFolders.Where(b => b.EventType == effect.EventType))
        {
            var path = System.Text.RegularExpressions.Regex.Replace(
                binding.PathTemplate, "\\{([^}]*)\\}",
                match => effect.String(match.Groups[1].Value) ?? "");
            if (path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Length != binding.PathTemplate.Split('/', StringSplitOptions.RemoveEmptyEntries).Length)
                continue;   // a placeholder rendered empty — never file into a collapsed path
            await DocumentAccess.EnsureFoldersAsync(tam, path, ct);
        }
    }
}
