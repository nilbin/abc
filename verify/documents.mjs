// tam.documents wire suite (docs/35): folder tree + reach ACLs end to end.
// alva = admin (*, documents.manage implied); tekla = technician (documents.read only).
import crypto from 'node:crypto';
const BASE = 'http://localhost:5100';
const b64url = (b) => b.toString('base64').replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
async function pkce(email, tenant) {
  const verifier = b64url(crypto.randomBytes(32));
  const challenge = b64url(crypto.createHash('sha256').update(verifier).digest());
  const jar = new Map(); const ck = () => [...jar.values()].join('; ');
  const sc = (r) => { for (const c of r.headers.getSetCookie?.() ?? []) { const p = c.split(';')[0]; jar.set(p.split('=')[0], p); } };
  const au = `${BASE}/connect/authorize?client_id=tam-spa&response_type=code&redirect_uri=${encodeURIComponent('http://localhost:5173/callback')}&code_challenge=${challenge}&code_challenge_method=S256&tenant=${tenant}`;
  let r = await fetch(au, {redirect:'manual'}); sc(r);
  r = await fetch(`${BASE}/connect/authorize/login`, {method:'POST',redirect:'manual',
    headers:{'content-type':'application/x-www-form-urlencoded',cookie:ck()},
    body:new URLSearchParams({email,password:'demo123',returnUrl:au.slice(BASE.length)})}); sc(r);
  let next = r.headers.get('location'), code = null;
  for (let i=0;i<6&&next&&!code;i++){const u = next.startsWith('http')?next:BASE+next;
    if (u.startsWith('http://localhost:5173/callback')){code=new URL(u).searchParams.get('code');break;}
    r = await fetch(u,{redirect:'manual',headers:{cookie:ck()}}); sc(r); next=r.headers.get('location');}
  const tr = await fetch(`${BASE}/connect/token`,{method:'POST',headers:{'content-type':'application/x-www-form-urlencoded'},
    body:new URLSearchParams({grant_type:'authorization_code',client_id:'tam-spa',code,redirect_uri:'http://localhost:5173/callback',code_verifier:verifier})});
  return (await tr.json()).access_token;
}
const view = (t,id,p='') => fetch(`${BASE}/api/views/${id}${p}`,{headers:{authorization:`Bearer ${t}`}}).then(async r=>({status:r.status, body:await r.json().catch(()=>null)}));
const op = (t,id,input) => fetch(`${BASE}/api/operations/${id}`,{method:'POST',
  headers:{authorization:`Bearer ${t}`,'content-type':'application/json','idempotency-key':crypto.randomUUID()},
  body:JSON.stringify(input)}).then(async r=>({status:r.status, body:await r.json().catch(()=>null)}));
const results = [];
const check = (n, ok, d) => { results.push(ok); console.log(`${ok?'PASS':'FAIL'}  ${n}  ${d??''}`); };
const codeOf = (r) => (r.body?.findings ?? []).map(f=>f.code).join(',');

const alva = await pkce('alva','demo');    // admin: * → documents.manage
const tekla = await pkce('tekla','demo');  // technician: documents.read only

// Seeded tree: /avtal (role:dispatcher) + /instruktioner (open).
const alvaFolders = await view(alva,'documents.folders.list');
const paths = (alvaFolders.body?.rows ?? []).map(r => r.path);
check('admin sees the whole tree', paths.includes('/avtal') && paths.includes('/instruktioner'), paths.join(','));
const avtal = alvaFolders.body.rows.find(r => r.path === '/avtal');
check('/avtal is marked shared', avtal?.shared === true, '');

// Tekla is NOT a dispatcher: the role-reach ACL hides /avtal, /instruktioner stays open.
const teklaFolders = await view(tekla,'documents.folders.list');
const teklaPaths = (teklaFolders.body?.rows ?? []).map(r => r.path);
check('role reach hides /avtal from a non-dispatcher', !teklaPaths.includes('/avtal'), teklaPaths.join(','));
check('unrestricted folder stays visible', teklaPaths.includes('/instruktioner'), '');

// Documents follow their folder's visibility; the seeded order attachment is queryable.
const teklaDocs = await view(tekla,'documents.list');
const teklaFiles = (teklaDocs.body?.rows ?? []).map(r => r.fileName);
check('documents in the hidden folder are hidden too', !teklaFiles.includes('ramavtal-acme.txt'), teklaFiles.join(','));
const attached = (teklaDocs.body?.rows ?? []).find(r => r.attachedTo?.startsWith('order:'));
check('the order-attached instruction is visible with its EntityRef', !!attached, attached?.attachedTo ?? '');
const byRef = await view(tekla,'documents.list',`?attachedTo=${encodeURIComponent(attached.attachedTo)}`);
check('attachedTo query returns exactly the record documents', byRef.body?.rows?.length === 1
  && byRef.body.rows[0].fileName === 'pumpservice-instruktion.txt', `rows=${byRef.body?.rows?.length}`);

// mkdir -p + share with a USER reach; content round-trips through upload + download.
const defined = await op(alva,'documents.folders.define',{path: '/projekt/2026'});
check('folders.define creates the path', defined.status === 200 && defined.body?.output?.path === '/projekt/2026',
  `status=${defined.status} path=${defined.body?.output?.path}`);
const folderId = defined.body.output.folderId;
const content = Buffer.from('Projektplan 2026 — utkast.').toString('base64');
const uploaded = await op(alva,'documents.upload',{folderId, fileName: 'projektplan.txt',
  contentBase64: content, contentType: 'text/plain'});
check('upload succeeds', uploaded.status === 200 && uploaded.body?.output?.size > 0, `status=${uploaded.status}`);
const dl = await fetch(`${BASE}/api/documents/${uploaded.body.output.documentId}/content`,
  {headers:{authorization:`Bearer ${alva}`}});
check('download round-trips the content', dl.status === 200
  && Buffer.from(await dl.arrayBuffer()).toString() === 'Projektplan 2026 — utkast.', `status=${dl.status}`);

// Streaming upload (docs/36 stage-then-intend): bytes go multipart to the staging endpoint
// (no base64, capacity-gated there); the WRITE is the ordinary intent referencing the hash.
const stagedBytes = 'Stort dokument — multipart, ingen base64.';
const fd = new FormData();
fd.append('file', new Blob([Buffer.from(stagedBytes)], {type: 'text/plain'}), 'stor-fil.txt');
const staged = await fetch(`${BASE}/api/documents/staging`,
  {method: 'POST', headers: {authorization: `Bearer ${alva}`}, body: fd})
  .then(async r => ({status: r.status, body: await r.json().catch(()=>null)}));
check('multipart staging returns the content hash', staged.status === 200 && !!staged.body?.contentHash,
  `status=${staged.status}`);
const teklaFd = new FormData();
teklaFd.append('file', new Blob([Buffer.from('x')]), 'x.txt');
const teklaStage = await fetch(`${BASE}/api/documents/staging`,
  {method: 'POST', headers: {authorization: `Bearer ${tekla}`}, body: teklaFd});
check('staging requires documents.add', teklaStage.status === 403, `status=${teklaStage.status}`);
const hashUp = await op(alva,'documents.upload',{folderId, fileName: 'stor-fil.txt',
  contentHash: staged.body?.contentHash, contentType: 'text/plain'});
check('upload by hash rides the pipeline', hashUp.status === 200 && hashUp.body?.output?.size > 0,
  `status=${hashUp.status} ${codeOf(hashUp)}`);
const dlStaged = await fetch(`${BASE}/api/documents/${hashUp.body?.output?.documentId}/content`,
  {headers:{authorization:`Bearer ${alva}`}});
check('staged content round-trips', dlStaged.status === 200
  && Buffer.from(await dlStaged.arrayBuffer()).toString() === stagedBytes, `status=${dlStaged.status}`);
const bogusUp = await op(alva,'documents.upload',{folderId, fileName: 'spok.txt',
  contentHash: 'deadbeef'.repeat(8)});
check('an unstaged hash fails closed', codeOf(bogusUp).includes('documents.invalid-content'), codeOf(bogusUp));

// Share /projekt with tekla by USER reach — the subtree (/projekt/2026 inherits) opens for her.
const projekt = await op(alva,'documents.folders.define',{path: '/projekt'});
const users = await view(alva,'users.list').then(r => r.body?.rows ?? []);
const teklaActor = users.find(u => (u.email ?? '').includes('tekla'))?.accountId
  ?? users.find(u => (u.name ?? u.displayName ?? '').toLowerCase().includes('tekla'))?.accountId;
// Fallback: share by role reach with the technician role instead if user id shape differs.
const shareRef = teklaActor ? `user:${teklaActor}` : 'role:technician';
const shared = await op(alva,'documents.folders.share',{folderId: projekt.body.output.folderId, reach: shareRef});
check(`share /projekt (${shareRef.split(':')[0]} reach) succeeds`, shared.status === 200, `status=${shared.status} ${codeOf(shared)}`);
// The share is DESCRIBED (docs/35 D-R6): the shares view labels the stored ref with the
// person's name (or the role's own name when the user-id fallback was taken).
const projShares = await view(alva,'documents.folders.shares',`?folderId=${projekt.body.output.folderId}`);
check('shares view describes the grant with a display label',
  (projShares.body?.rows ?? []).some(r => r.reach === shareRef
    && (r.label === 'Tekla Nilsson' || r.label === 'technician')),
  JSON.stringify(projShares.body?.rows ?? []));
const teklaAfter = await view(tekla,'documents.folders.list');
const teklaPathsAfter = (teklaAfter.body?.rows ?? []).map(r => r.path);
check('ACL inheritance: /projekt/2026 visible via the parent share', teklaPathsAfter.includes('/projekt/2026'),
  teklaPathsAfter.join(','));
// tekla lacks the share BEFORE this: she must NOT see /avtal still (unchanged) — regression guard.
check('/avtal still hidden after unrelated share', !teklaPathsAfter.includes('/avtal'), '');

// Fail-closed seams: unknown reach kind is a teaching finding; a kind from an INACTIVE
// plugin (approvals is not activated for demo) shares fine as data but reaches nobody.
const badShare = await op(alva,'documents.folders.share',{folderId, reach: 'crew:abc'});
check('unknown reach kind rejected with documents.unknown-reach', codeOf(badShare).includes('documents.unknown-reach'), codeOf(badShare));
// Giving the CHILD its own ACL overrides inheritance (nearest-ancestor-with-rows): the
// inert ref (approvals is not active for demo) is now /projekt/2026's WHOLE effective ACL,
// so it reaches nobody but admins — the child locked tighter than its parent, fail-closed.
const inertRef = 'approvals.group:'+crypto.randomUUID();
const inertShare = await op(alva,'documents.folders.share',{folderId, reach: inertRef});
check('inactive-plugin kind stores as inert data', inertShare.status === 200, `status=${inertShare.status} ${codeOf(inertShare)}`);
const teklaInert = await view(tekla,'documents.folders.list');
const inertPaths = (teklaInert.body?.rows ?? []).map(r => r.path);
check('own ACL overrides the parent share (child locked tighter, inert kind reaches nobody)',
  !inertPaths.includes('/projekt/2026') && inertPaths.includes('/projekt'), inertPaths.join(','));
// The share dialog's list view: one folder's OWN grants, admin-only.
const sharesList = await view(alva,'documents.folders.shares',`?folderId=${folderId}`);
check('folders.shares lists the folder\'s own grants', sharesList.status === 200
  && (sharesList.body?.rows ?? []).map(r => r.reach).includes(inertRef), `status=${sharesList.status}`);
const teklaShares = await view(tekla,'documents.folders.shares',`?folderId=${folderId}`);
check('folders.shares requires documents.manage', teklaShares.status === 403, `status=${teklaShares.status}`);
const unshared = await op(alva,'documents.folders.unshare',{folderId, reach: inertRef});
check('unshare restores inheritance from the parent', unshared.status === 200
  && ((await view(tekla,'documents.folders.list')).body?.rows ?? []).some(r => r.path === '/projekt/2026'),
  `status=${unshared.status}`);
check('folders.shares empties after unshare',
  ((await view(alva,'documents.folders.shares',`?folderId=${folderId}`)).body?.rows ?? []).length === 0, '');

// Retire hides from listings.
const retired = await op(alva,'documents.retire',{documentId: uploaded.body.output.documentId});
check('documents.retire succeeds', retired.status === 200, `status=${retired.status}`);
const afterRetire = await view(alva,'documents.list','?search=projektplan');
check('retired document leaves the listing', (afterRetire.body?.rows ?? []).length === 0, `rows=${afterRetire.body?.rows?.length}`);
const dlRetired = await fetch(`${BASE}/api/documents/${uploaded.body.output.documentId}/content`,
  {headers:{authorization:`Bearer ${alva}`}});
check('retired content 404s', dlRetired.status === 404, `status=${dlRetired.status}`);

// Magic folder (docs/35): creating an order materializes /order/{number} via the outbox.
const custs = await view(alva,'customers.list','?pageSize=5');
const orderRes = await op(alva,'orders.create',{customerId: custs.body.rows[0].id,
  orderType: 'service', workAddress: 'Testgatan 1', description: 'Magic folder-test'});
check('orders.create succeeds', orderRes.status === 200, `status=${orderRes.status} ${codeOf(orderRes)}`);
const orderNumber = orderRes.body?.output?.number;
let magic = false;
for (let i = 0; i < 20 && !magic; i++) {           // outbox dispatch is async — poll briefly
  await new Promise(r => setTimeout(r, 500));
  const tree = await view(alva,'documents.folders.list',`?search=${encodeURIComponent('/order/'+orderNumber)}`);
  magic = (tree.body?.rows ?? []).some(r => r.path === `/order/${orderNumber}`);
}
check(`magic folder /order/${orderNumber} materialized from order-created`, magic, '');

console.log(results.every(Boolean) ? `ALL ${results.length} PASS` : `${results.filter(Boolean).length}/${results.length} passed`);
process.exit(results.every(Boolean) ? 0 : 1);
