// docs/34 M2 on the wire: WorkOrder — the 5-state intent machine, own-scope paired atoms,
// assignment via membership-resolved derivation options, custom field on a NEW entity.
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
const results = [];
const check = (n, ok, d) => { results.push(ok); console.log(`${ok?'PASS':'FAIL'}  ${n}  ${d??''}`); };
const op = (t, id, body) => fetch(`${BASE}/api/operations/${id}`, {method:'POST',
  headers:{authorization:`Bearer ${t}`,'content-type':'application/json'},
  body: JSON.stringify(body)}).then(async r=>({status:r.status, body:await r.json()}));
const view = (t, id, q='') => fetch(`${BASE}/api/views/${id}${q}`,
  {headers:{authorization:`Bearer ${t}`}}).then(r=>r.json());
const resolve = (t, formId, input, changed) => fetch(`${BASE}/api/forms/${formId}/resolve`, {method:'POST',
  headers:{authorization:`Bearer ${t}`,'content-type':'application/json'},
  body: JSON.stringify({input, changed, revision: 1})}).then(r=>r.json());

const didrik = await pkce('didrik','demo');
const tekla = await pkce('tekla','demo');

// docs/34 M6 on the wire: inspect v2 — checklist templates by order type, auto-instantiation
// via order-created, per-item check-off, MANDATORY-blocks-completion gate.
const m = await fetch(`${BASE}/api/manifest`,{headers:{authorization:`Bearer ${didrik}`}}).then(r=>r.json());

// 1) manifest surface: ops, pages, gate, event contract, slot panels
const ops = Object.keys(m.operations ?? {});
const wanted = ['inspect.templates.define','inspect.templates.add-item','inspect.templates.retire',
  'inspect.items.check','inspect.items.uncheck'];
check('manifest: inspect v2 operations declared', wanted.every(o=>ops.includes(o)),
  ops.filter(o=>o.startsWith('inspect.')).join(','));
check('manifest: templates + checklists pages', !!m.pages?.['inspect.templates'] && !!m.pages?.['inspect.checklists'],
  Object.keys(m.pages??{}).filter(p=>p.startsWith('inspect')).join(','));
check('manifest: order-created event contract', JSON.stringify(m.events??m.eventContracts??{}).includes('order-created'), '');
const mstr = JSON.stringify(m);
check('manifest: orders.complete gated by inspect', /gatedBy[^\]]*inspect/.test(mstr), '');
check('manifest: two inspect panels on web.orders.detail', (mstr.match(/web\.orders\.detail/g)??[]).length >= 2, '');

// 2) the seeded mandatory checklist BLOCKS completion of its open service order
const orders = await view(didrik,'orders.list','?pageSize=100');
const o1415 = (orders.rows??[]).find(r=>String(r.number)==='2026-01415');
check('seeded order 2026-01415 present', !!o1415, (orders.rows??[]).map(r=>r.number).join(','));
const blocked = await op(didrik,'orders.complete',{orderId: o1415?.id});
check('complete blocked → 422 inspect.checklist-incomplete', blocked.status===422
  && JSON.stringify(blocked.body).includes('inspect.checklist-incomplete'), JSON.stringify(blocked.body).slice(0,160));

// 3) check off the mandatory items; the last check passes the checklist
const cls = await view(didrik,'inspect.checklists.list',`?orderId=${o1415?.id}`);
const mand = (cls.rows??[]).find(r=>r.mandatory), soft = (cls.rows??[]).find(r=>!r.mandatory);
check('order carries mandatory + non-mandatory checklists', !!mand && !!soft, JSON.stringify(cls).slice(0,200));
const items = (await view(didrik,'inspect.items.list',`?checklistId=${mand?.id}`)).rows ?? [];
check('mandatory checklist has 3 open items', items.length===3 && items.every(i=>!i.done), JSON.stringify(items).slice(0,160));
let lastCheck = null;
for (const it of items) lastCheck = await op(didrik,'inspect.items.check',{itemId: it.id});
check('last check passes the checklist', lastCheck?.status===200
  && JSON.stringify(lastCheck.body).match(/checklistPassed":true|inspect.checklist-passed/), JSON.stringify(lastCheck?.body).slice(0,160));

// 4) uncheck re-opens the gate; re-check restores; complete then SUCCEEDS with the
//    non-mandatory checklist still open (it never blocks)
const un = await op(didrik,'inspect.items.uncheck',{itemId: items[0].id});
const reblocked = await op(didrik,'orders.complete',{orderId: o1415?.id});
check('uncheck re-blocks completion', un.status===200 && reblocked.status===422, `${un.status}/${reblocked.status}`);
await op(didrik,'inspect.items.check',{itemId: items[0].id});
const softItems = (await view(didrik,'inspect.items.list',`?checklistId=${soft?.id}`)).rows ?? [];
const done = await op(didrik,'orders.complete',{orderId: o1415?.id});
check('complete succeeds with non-mandatory still open', done.status===200
  && softItems.some(i=>!i.done), JSON.stringify(done.body).slice(0,120));

// 5) auto-instantiation: a NEW service order grows both templates' checklists via the outbox
const customers = await view(didrik,'customers.list');
const anyCustomer = (customers.rows??[])[0];
const created = await op(didrik,'orders.create',{customerId: anyCustomer?.id, orderType:'service',
  workAddress:'Verkstadsgatan 1', description:'M6 wire probe service order'});
check('service order created + publishes order-created', created.status===200
  && JSON.stringify(created.body).includes('order-created'), JSON.stringify(created.body).slice(0,160));
const newId = created.body.output?.orderId;
let grown = [];
for (let i=0;i<20 && grown.length<2;i++){ await new Promise(r=>setTimeout(r,500));
  grown = (await view(didrik,'inspect.checklists.list',`?orderId=${newId}`)).rows ?? []; }
const gm = grown.find(r=>r.mandatory), gs = grown.find(r=>!r.mandatory);
check('outbox instantiated both templates (3 + 2 items)', grown.length===2
  && gm?.openItems===3 && gs?.openItems===2, JSON.stringify(grown).slice(0,200));

// 6) a PROJECT order matches no template → no checklists
// (arc 4b added the project-belongs-to-customer guard: pick the PROJECT's own customer)
const proj = ((await view(didrik,'projects.list','?pageSize=50')).rows??[]).find(p=>String(p.status??'open').match(/open/i))
  ?? ((await view(didrik,'projects.list','?pageSize=50')).rows??[])[0];
const projCustomer = ((await view(didrik,'customers.lookup',`?search=${encodeURIComponent(proj?.customerName??'')}`)).rows??[])[0];
const projOrder = await op(didrik,'orders.create',{customerId: projCustomer?.id ?? anyCustomer?.id, orderType:'project',
  projectId: proj?.id, workAddress:'Verkstadsgatan 2', description:'M6 wire probe project order'});
await new Promise(r=>setTimeout(r,3000));
const none = (await view(didrik,'inspect.checklists.list',`?orderId=${projOrder.body.output?.orderId}`)).rows ?? [];
check('project order gets no checklists', projOrder.status===200 && none.length===0, JSON.stringify(none).slice(0,120));

// 7) retiring a template stops instantiation for FUTURE orders only
const templates = (await view(didrik,'inspect.templates.list')).rows ?? [];
const softTpl = templates.find(t=>!t.mandatory && !t.retired);
const retired = await op(didrik,'inspect.templates.retire',{templateId: softTpl?.id});
const after = await op(didrik,'orders.create',{customerId: anyCustomer?.id, orderType:'service',
  workAddress:'Verkstadsgatan 3', description:'M6 wire probe after retire'});
let afterRows = [];
for (let i=0;i<20;i++){ await new Promise(r=>setTimeout(r,500));
  afterRows = (await view(didrik,'inspect.checklists.list',`?orderId=${after.body.output?.orderId}`)).rows ?? [];
  if (afterRows.length>=1 && i>=6) break; }
check('retired template no longer instantiates', retired.status===200
  && afterRows.length===1 && afterRows[0].mandatory===true, JSON.stringify(afterRows).slice(0,160));

console.log(`\n${results.filter(Boolean).length}/${results.length} checks passed`);
process.exit(results.every(Boolean)?0:1);
