// docs/22 rule-builder schema: the rules.schema view over the wire — the server-authoritative
// row-field types the visual builder renders value controls from. Reuses the PKCE login flow.
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
const results = [];
const check = (n, ok, d) => { results.push(ok); console.log(`${ok?'PASS':'FAIL'}  ${n}  ${d??''}`); };

const alva = await pkce('alva','demo');

// An operation whose single {entity}Id names the target row: projects.close → project.
const closeSchema = await view(alva,'rules.schema','?trigger=projects.close&kind=operation&pageSize=200');
const rows = closeSchema.body?.rows ?? [];
const byPath = Object.fromEntries(rows.map(r => [r.path, r]));
check('rules.schema reachable (200) for projects.close', closeSchema.status === 200, `status=${closeSchema.status}`);
check('every row names the target entity', rows.length > 0 && rows.every(r => r.entityKey === 'project'),
  `entities=${[...new Set(rows.map(r=>r.entityKey))].join(',')}`);
check('row.status is a string carrying its enum options',
  byPath['row.status']?.wireKind === 'string' && (byPath['row.status']?.options?.length ?? 0) > 0,
  `wireKind=${byPath['row.status']?.wireKind} options=${(byPath['row.status']?.options??[]).join('|')}`);
check('row.budget is a number', byPath['row.budget']?.wireKind === 'number', `wireKind=${byPath['row.budget']?.wireKind}`);
check('the extension bag is not offered as a reference', !('row.extensions' in byPath), Object.keys(byPath).join(','));

// A create carries no single {entity}Id (customerId + projectId) → RUL004: no row fields.
const createSchema = await view(alva,'rules.schema','?trigger=orders.create&kind=operation');
check('orders.create (no single target row) yields an empty schema',
  createSchema.status === 200 && (createSchema.body?.rows?.length ?? -1) === 0,
  `status=${createSchema.status} rows=${createSchema.body?.rows?.length}`);

// An EVENT trigger whose payload carries orderId → the order row, typed from the model.
const eventSchema = await view(alva,'rules.schema','?trigger=order-created&kind=event&pageSize=200');
const eventRows = eventSchema.body?.rows ?? [];
check('event trigger order-created resolves its target row (order)',
  eventSchema.status === 200 && eventRows.length > 0 && eventRows.every(r => r.entityKey === 'order'),
  `status=${eventSchema.status} rows=${eventRows.length} entity=${eventRows[0]?.entityKey}`);

// Unknown trigger is empty, never a 500.
const unknown = await view(alva,'rules.schema','?trigger=nope.none&kind=operation');
check('unknown trigger → empty, not an error', unknown.status === 200 && (unknown.body?.rows?.length ?? -1) === 0,
  `status=${unknown.status}`);

// The form's OWN dynamics (docs/05 dogfooded): resolve web.rules.define and watch VisibleWhen /
// RequiredWhen / the target-fields derivation respond to the trigger and action.
const resolve = (input, changed) => fetch(`${BASE}/api/forms/web.rules.define/resolve`,{method:'POST',
  headers:{authorization:`Bearer ${alva}`,'content-type':'application/json'},
  body:JSON.stringify({input, changed, revision:1})}).then(async r=>({status:r.status, body:await r.json().catch(()=>null)}));

const noTrigger = await resolve({}, ['onOperation']);
check('no trigger → condition/action/targetField hidden',
  noTrigger.status === 200
    && noTrigger.body?.fields?.condition?.visible === false
    && noTrigger.body?.fields?.action?.visible === false
    && noTrigger.body?.fields?.targetField?.visible === false,
  `status=${noTrigger.status} condition.visible=${noTrigger.body?.fields?.condition?.visible}`);

const withTrigger = await resolve({onOperation:'projects.close'}, ['onOperation']);
const targetOptions = withTrigger.body?.fields?.targetField?.options ?? [];
check('trigger chosen → builder fields visible, messages required (finding rule)',
  withTrigger.body?.fields?.condition?.visible === true
    && withTrigger.body?.fields?.messages?.required === true
    && withTrigger.body?.fields?.targetField?.visible === true,
  `condition.visible=${withTrigger.body?.fields?.condition?.visible} messages.required=${withTrigger.body?.fields?.messages?.required}`);
check('targetField offers the trigger input fields as localized options (derivation)',
  targetOptions.some(o => o.value === 'projectId') && targetOptions.every(o => typeof o.label === 'string'),
  targetOptions.map(o=>`${o.value}`).join(','));

const withAction = await resolve({onOperation:'projects.close', action:'{"type":"publish-event"}'}, ['onOperation']);
check('action rule → targetField hidden, messages optional',
  withAction.body?.fields?.targetField?.visible === false
    && withAction.body?.fields?.messages?.required === false,
  `targetField.visible=${withAction.body?.fields?.targetField?.visible} messages.required=${withAction.body?.fields?.messages?.required}`);

// ResetOn + RowForm in the manifest (docs/05, docs/32): the trigger pair is a mutual ResetOn,
// dependent authoring resets on the trigger, and the rules grid declares the edit row form.
const manifest = await fetch(`${BASE}/api/manifest`,{headers:{authorization:`Bearer ${alva}`}}).then(r=>r.json());
const defForm = manifest.forms?.['web.rules.define'];
const f = (n) => (defForm?.fields ?? []).find(x => x.name === n);
check('manifest: mutual ResetOn pair on the triggers',
  f('onOperation')?.resetOn?.includes('onEvent') && f('onEvent')?.resetOn?.includes('onOperation'),
  JSON.stringify({op: f('onOperation')?.resetOn, ev: f('onEvent')?.resetOn}));
check('manifest: condition/action reset on either trigger',
  ['condition','action'].every(n => ['onOperation','onEvent'].every(t => f(n)?.resetOn?.includes(t))),
  JSON.stringify({condition: f('condition')?.resetOn, action: f('action')?.resetOn}));
check('manifest: rules grid declares the edit row form (unified actions)',
  (manifest.grids?.['web.rules']?.actions ?? []).some(a =>
    a.operation === 'rules.define' && a.placement === 'row' && a.mode === 'form'),
  JSON.stringify(manifest.grids?.['web.rules']?.actions));
check('manifest: orders.create projectId resets on customerId (second consumer)',
  (manifest.forms?.['web.orders.create']?.fields ?? []).find(x => x.name === 'projectId')?.resetOn?.includes('customerId'),
  JSON.stringify((manifest.forms?.['web.orders.create']?.fields ?? []).find(x => x.name === 'projectId')?.resetOn));

// The edit round-trip over the wire: define → list carries the definition → redefine → updated.
const opRules = (t,id,i) => fetch(`${BASE}/api/operations/${id}`,{method:'POST',
  headers:{authorization:`Bearer ${t}`,'content-type':'application/json'},
  body:JSON.stringify(i)}).then(async r=>({status:r.status, body:await r.json().catch(()=>null)}));
const mkRule = (v) => opRules(alva,'rules.define',{name:'wire-editable', onOperation:'projects.close',
  condition: JSON.stringify({t:'bin',op:'gt',l:{t:'field',f:'row.budget'},r:{t:'const',v}}),
  messages:{sv:'Stopp',en:'Stop'}});
await mkRule(5);
const listed = await view(alva,'rules.list','?search=wire-editable');
const listedRow = listed.body?.rows?.[0];
check('rules.list carries the definition for the edit form',
  typeof listedRow?.condition === 'string' && listedRow.condition.includes('row.budget')
    && listedRow?.messages?.sv === 'Stopp',
  JSON.stringify({condition: !!listedRow?.condition, sv: listedRow?.messages?.sv}));
await mkRule(9);
const relisted = await view(alva,'rules.list','?search=wire-editable');
check('re-define under the same name edits in place (one row, new condition)',
  relisted.body?.rows?.length === 1 && relisted.body.rows[0].condition.includes('9'),
  `rows=${relisted.body?.rows?.length}`);
await opRules(alva,'rules.retire',{name:'wire-editable'});

console.log(`\n${results.filter(Boolean).length}/${results.length} passed`);
process.exit(results.every(Boolean) ? 0 : 1);
