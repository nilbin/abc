import { useState } from 'react';
import { useEffect } from 'react';
import {
  ActionIcon, Alert, Button, Group, Modal, MultiSelect, NavLink, Paper, Stack, Table, Text, Title,
} from '@mantine/core';
import { OperationForm, useTam, useView } from '@tam/react';

/**
 * The documents tree browser (docs/35) — the app's ONE registered page, and deliberately so:
 * a two-pane tree browser is genuinely custom UX (docs/32 D-P2's escape hatch used as
 * intended). Every capability it composes is the framework's: useView over the ACL-filtered
 * listings, OperationForm for folder/upload intents (the "file" renderer fills the upload
 * contract), authorized downloads through client.blob.
 */
export function DocumentsBrowser() {
  const { t, can, client, invalidate } = useTam();
  const [selected, setSelected] = useState<{ id: string; path: string } | null>(null);
  const [dialog, setDialog] = useState<'folder' | 'upload' | 'share' | null>(null);
  const folders = useView('documents.folders.list', {});
  const files = useView('documents.list', selected ? { folderId: selected.id } : {});

  const rows = (files.data?.rows ?? []) as Record<string, unknown>[];
  const tree = ((folders.data?.rows ?? []) as { id: string; path: string; name: string; shared: boolean }[])
    .slice()
    .sort((a, b) => a.path.localeCompare(b.path));

  const download = async (id: string, fileName: string) => {
    const blob = await client.blob(`/api/documents/${id}/content`);
    if (!blob) return;
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    anchor.click();
    URL.revokeObjectURL(url);
  };

  return (
    <Stack gap="md">
      <Group justify="space-between">
        <Title order={3}>{t('nav.documents')}</Title>
        <Group gap="xs">
          {can('documents.manage') && (
            <Button variant="light" size="xs" onClick={() => setDialog('folder')}>
              {t('operations.documents.folders.define.title')}
            </Button>
          )}
          {can('documents.manage') && selected && (
            <Button variant="light" size="xs" onClick={() => setDialog('share')}>
              {t('operations.documents.folders.share.title')}
            </Button>
          )}
          {can('documents.add') && selected && (
            <Button size="xs" onClick={() => setDialog('upload')}>
              {t('operations.documents.upload.title')}
            </Button>
          )}
        </Group>
      </Group>

      <Group align="flex-start" gap="lg" wrap="nowrap">
        <Paper withBorder p="xs" w={280}>
          <NavLink label="/" active={selected === null} onClick={() => setSelected(null)} />
          {tree.map(folder => (
            <NavLink
              key={folder.id}
              label={folder.name}
              description={folder.shared ? t('labels.shared') : undefined}
              active={selected?.id === folder.id}
              style={{ paddingLeft: 12 * folder.path.split('/').length }}
              onClick={() => setSelected({ id: folder.id, path: folder.path })}
            />
          ))}
        </Paper>

        <Stack gap="xs" flex={1}>
          {selected && <Text size="sm" c="dimmed">{selected.path}</Text>}
          {rows.length === 0
            ? <Alert variant="light">{t('grid.empty')}</Alert>
            : (
              <Table striped highlightOnHover>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('labels.file-name')}</Table.Th>
                    <Table.Th>{t('labels.folder')}</Table.Th>
                    <Table.Th>{t('labels.uploaded-by')}</Table.Th>
                    <Table.Th />
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {rows.map(row => (
                    <Table.Tr key={String(row.id)}>
                      <Table.Td>{String(row.fileName)}</Table.Td>
                      <Table.Td>{String(row.folderPath)}</Table.Td>
                      <Table.Td>{String(row.uploadedByName ?? '')}</Table.Td>
                      <Table.Td>
                        <ActionIcon variant="subtle" aria-label={String(row.fileName)}
                          onClick={() => void download(String(row.id), String(row.fileName))}>
                          ↓
                        </ActionIcon>
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
        </Stack>
      </Group>

      <Modal opened={dialog === 'folder'} onClose={() => setDialog(null)}
        title={t('operations.documents.folders.define.title')}>
        <OperationForm form="web.documents.folders.define"
          onSuccess={r => { setDialog(null); invalidate(r.effects); }} />
      </Modal>
      <Modal opened={dialog === 'upload'} onClose={() => setDialog(null)}
        title={t('operations.documents.upload.title')}>
        <OperationForm form="web.documents.upload"
          initialValues={{ folderId: selected?.id }}
          onSuccess={r => { setDialog(null); invalidate(r.effects); }} />
      </Modal>
      <Modal opened={dialog === 'share'} onClose={() => setDialog(null)}
        title={t('operations.documents.folders.share.title')}>
        {selected && <FolderShares folderId={selected.id} />}
      </Modal>
    </Stack>
  );
}

/**
 * The share dialog's body — ONE control: a multi-select whose pills ARE the folder's own
 * grants (described labels from documents.folders.shares). Picking adds a grant, removing a
 * pill revokes it — each an immediate intent (share/unshare), no separate list or submit.
 * Inherited/open access shows as the empty-state hint; the effective-ACL question stays
 * server-side (docs/35).
 */
function FolderShares({ folderId }: { folderId: string }) {
  const { t, client, invalidate } = useTam();
  const shares = useView('documents.folders.shares', { folderId });
  const current = (shares.data?.rows ?? []) as { id: string; reach: string; label?: string | null }[];
  const value = current.map(g => g.reach);

  // Server-searched options, grouped by kind — the same shape as the reach form renderer.
  const [search, setSearch] = useState('');
  const [debounced, setDebounced] = useState('');
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(search), search ? 200 : 0);
    return () => clearTimeout(timer);
  }, [search]);
  const options = useView('reach.search', { search: debounced || undefined, pageSize: 50 });
  const rows = (options.data?.rows ?? []) as { id: string; label: string; kind: string }[];
  const [busy, setBusy] = useState(false);

  const heading = (kind: string) => {
    const localized = t(`reach.kinds.${kind}`);
    return localized === `reach.kinds.${kind}` ? kind : localized;
  };
  const groups = new Map<string, { value: string; label: string }[]>();
  const add = (kind: string, ref: string, label: string) => {
    const h = heading(kind);
    if (!groups.has(h)) groups.set(h, []);
    groups.get(h)!.push({ value: ref, label });
  };
  for (const row of rows) add(row.kind, row.id, row.label);
  // Current grants must exist in `data` for their pills to render labeled — union any the
  // search page did not happen to include (their described label, else the canonical ref).
  const known = new Set(rows.map(r => r.id));
  for (const grant of current) {
    if (known.has(grant.reach)) continue;
    add(grant.reach.split(':')[0], grant.reach, grant.label ?? grant.reach);
  }
  const data = [...groups.entries()].map(([group, items]) => ({ group, items }));

  // The selection delta IS the intent stream: added ref → share, removed ref → unshare.
  const apply = async (next: string[]) => {
    setBusy(true);
    try {
      for (const ref of next.filter(r => !value.includes(r)))
        invalidate((await client.operation('documents.folders.share',
          { folderId, reach: ref }, { idempotencyKey: crypto.randomUUID() })).effects);
      for (const ref of value.filter(r => !next.includes(r)))
        invalidate((await client.operation('documents.folders.unshare',
          { folderId, reach: ref }, { idempotencyKey: crypto.randomUUID() })).effects);
    } finally {
      setBusy(false);
    }
  };

  return (
    <MultiSelect
      label={t('labels.shared-with')}
      description={value.length === 0 ? t('documents.no-own-shares') : undefined}
      data={data}
      value={value}
      onChange={v => void apply(v)}
      searchable
      onSearchChange={setSearch}
      filter={({ options: o }) => o /* the server already filtered */}
      nothingFoundMessage={t('common.no-matches')}
      disabled={busy}
    />
  );
}
