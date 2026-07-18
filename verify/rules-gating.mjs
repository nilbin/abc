// P5 automation rules running as tam.rules' PURE wildcard gate: define a rule, watch it block,
// retire it, watch it stop — the executor has no rules special case anymore.
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
const op = (t,id,i) => fetch(`${BASE}/api/operations/${id}`,{method:'POST',
  headers:{authorization:`Bearer ${t}`,'content-type':'application/json'},
  body:JSON.stringify(i)}).then(async r=>({status:r.status, body:await r.json().catch(()=>null)}));
const results = [];
const check = (n, ok, d) => { results.push(ok); console.log(`${ok?'PASS':'FAIL'}  ${n}  ${d??''}`); };
const codes = (r) => (r.body?.findings ?? []).map(f => f.code);

const [alva, didrik] = await Promise.all([pkce('alva','demo'), pkce('didrik','demo')]);
const customers = await view(didrik,'customers.lookup','?search=');
const customerId = customers.body?.rows?.[0]?.id;

// A tenant rule: orders.create with estimatedTotal >= 60 000 is blocked outright.
const condition = JSON.stringify({t:'bin', op:'ge', l:{t:'field', f:'estimatedTotal'}, r:{t:'const', v:60000}});
const rule = await op(alva,'rules.define',{name:'megaorder-stop', onOperation:'orders.create',
  condition, messages:{sv:'För stor order för självbetjäning.', en:'Order too large for self-service.'},
  targetField:'estimatedTotal'});
check('rule defined', rule.status === 200 && !!rule.body?.output?.ruleId, `status=${rule.status} ${codes(rule).join(',')}`);

const blocked = await op(didrik,'orders.create',{customerId, orderType:'Service', workAddress:'Storgatan 1',
  description:'Jätteprojekt', estimatedTotal:80000});
check('rules gate blocks pre-transaction', codes(blocked).includes('rules.megaorder-stop'), codes(blocked).join(','));
check('tenant-authored message in culture', (blocked.body?.findings??[]).some(f => f.message === 'För stor order för självbetjäning.'),
  (blocked.body?.findings??[]).map(f=>f.message).join('|'));

const ok = await op(didrik,'orders.create',{customerId, orderType:'Service', workAddress:'Storgatan 1',
  description:'Normal order', estimatedTotal:50000});
check('below the rule passes', ok.status === 200 && !!ok.body?.output, `status=${ok.status}`);

const retire = await op(alva,'rules.retire',{name:'megaorder-stop'});
check('rule retired', retire.status === 200, `status=${retire.status}`);
const after = await op(didrik,'orders.create',{customerId, orderType:'Service', workAddress:'Storgatan 1',
  description:'Jätteprojekt igen', estimatedTotal:80000});
check('retired rule no longer fires', after.status === 200 && !!after.body?.output, `status=${after.status} ${codes(after).join(',')}`);

// docs/22 row.* increment: a rule that reads the operation's TARGET row — Money compares
// as a number, the status enum as its wire string, exactly like input fields.
const bigP = await op(didrik,'projects.create',{customerId, number:'P-RULE-BIG', name:'Rule big', budget:200000});
const smallP = await op(didrik,'projects.create',{customerId, number:'P-RULE-SMALL', name:'Rule small', budget:5000});
const rowCondition = JSON.stringify({t:'bin', op:'and',
  l:{t:'bin', op:'gt', l:{t:'field', f:'row.budget'}, r:{t:'const', v:100000}},
  r:{t:'bin', op:'eq', l:{t:'field', f:'row.status'}, r:{t:'const', v:'open'}}});
const rowRule = await op(alva,'rules.define',{name:'big-project-close', onOperation:'projects.close',
  condition: rowCondition, messages:{sv:'Stora projekt stängs av ekonomi.', en:'Big projects are closed by finance.'}});
check('row.* rule defined (target resolved from projectId input)',
  rowRule.status === 200 && !!rowRule.body?.output?.ruleId, `status=${rowRule.status} ${codes(rowRule).join(',')}`);

const bigClose = await op(didrik,'projects.close',{projectId: bigP.body?.output?.projectId});
check('row condition blocks: budget > 100k AND status open', codes(bigClose).includes('rules.big-project-close'),
  `status=${bigClose.status} ${codes(bigClose).join(',')}`);
const smallClose = await op(didrik,'projects.close',{projectId: smallP.body?.output?.projectId});
check('small project closes under the same rule', smallClose.status === 200, `status=${smallClose.status} ${codes(smallClose).join(',')}`);

// RUL004: orders.create carries TWO {entity}Id inputs (customerId, projectId) — no single target.
const ambiguous = await op(alva,'rules.define',{name:'ambiguous-row', onOperation:'orders.create',
  condition: JSON.stringify({t:'bin', op:'eq', l:{t:'field', f:'row.name'}, r:{t:'const', v:'x'}}),
  messages:{sv:'x', en:'x'}});
check('RUL004: no single target row → rules.no-target-row', codes(ambiguous).includes('rules.no-target-row'),
  codes(ambiguous).join(','));

// RUL002 over the row namespace: unknown entity member rejected at define.
const bogus = await op(alva,'rules.define',{name:'bogus-row', onOperation:'projects.close',
  condition: JSON.stringify({t:'bin', op:'eq', l:{t:'field', f:'row.bogusField'}, r:{t:'const', v:'x'}}),
  messages:{sv:'x', en:'x'}});
check('RUL002 over row: unknown field rejected', codes(bogus).includes('rules.unknown-field'), codes(bogus).join(','));

const retireRow = await op(alva,'rules.retire',{name:'big-project-close'});
const bigClose2 = await op(didrik,'projects.close',{projectId: bigP.body?.output?.projectId});
check('retired row rule no longer blocks', retireRow.status === 200 && bigClose2.status === 200,
  `${retireRow.status}/${bigClose2.status} ${codes(bigClose2).join(',')}`);

// The SEEDED fn-node rule (docs/22 relative dates): the urgent order can't be scheduled
// more than 7 days out — the cutoff comes from {"t":"fn","op":"today","days":7}, not a
// baked-in date, so this check is stable on any run day.
const wos = await view(didrik,'orders.list','?pageSize=50');
const urgentWo = (wos.body?.rows ?? wos.rows ?? []).find(r=>r.number==='2026-01422');
const iso = (d)=>{const x=new Date(); x.setUTCDate(x.getUTCDate()+d); return x.toISOString().slice(0,10);};
// Assign to Didrik, not Tekla — fieldm2 asserts Tekla's own-scope count later in sequence.
const users = await view(didrik,'users.lookup','?search=Didrik');
const teklaId = (users.body?.rows ?? users.rows ?? [])[0]?.id;
const farOut = await op(didrik,'orders.schedule',{orderId: urgentWo?.id,
  scheduledDate: iso(10), assigneeActorId: String(teklaId)});
check('seeded fn-node rule blocks urgent schedule 10 days out', codes(farOut).includes('rules.urgent-schedule-window'),
  `status=${farOut.status} ${codes(farOut).join(',')}`);
const nearIn = await op(didrik,'orders.schedule',{orderId: urgentWo?.id,
  scheduledDate: iso(3), assigneeActorId: String(teklaId)});
check('urgent schedule 3 days out passes', nearIn.status===200, `status=${nearIn.status} ${codes(nearIn).join(',')}`);

// docs/22 ACTION catalog: a firing rule can DO something — set a registered extension
// field on the target row (transactional, same commit) or publish a derived event.
const actRule = await op(alva,'rules.define',{name:'urgent-needs-lift', onOperation:'orders.set-priority',
  condition: JSON.stringify({t:'bin', op:'eq', l:{t:'field', f:'priority'}, r:{t:'const', v:'urgent'}}),
  messages:{}, action: JSON.stringify({type:'set-field', field:'ext.requiresLift', value:true})});
check('action rule defined (set-field, validated against the registry)',
  actRule.status===200 && !!actRule.body?.output?.ruleId, `status=${actRule.status} ${codes(actRule).join(',')}`);
const reprioritized = await op(didrik,'orders.set-priority',{orderId: urgentWo?.id, priority:'urgent'});
check('operation succeeds — actions never block', reprioritized.status===200, `status=${reprioritized.status}`);
const woAfter = await view(didrik,'orders.list','?pageSize=50');
const woRow = (woAfter.body?.rows ?? woAfter.rows ?? []).find(r=>r.number==='2026-01422');
check('set-field action wrote the extension in the same commit', woRow?.extensions?.requiresLift===true,
  JSON.stringify(woRow?.extensions??{}));
const badAction = await op(alva,'rules.define',{name:'bad-action', onOperation:'orders.set-priority',
  condition: JSON.stringify({t:'bin', op:'eq', l:{t:'field', f:'priority'}, r:{t:'const', v:'urgent'}}),
  messages:{}, action: JSON.stringify({type:'set-field', field:'ext.nope', value:true})});
check('unregistered set-field target rejected (rules.invalid-action)', codes(badAction).includes('rules.invalid-action'),
  codes(badAction).join(','));
await op(alva,'rules.retire',{name:'urgent-needs-lift'});

// docs/22 EFFECT-triggered rules: a rule fired by a DOMAIN EVENT (order-created), its
// set-field action writing an extension on the referenced order — evaluated on the outbox
// dispatch path. Author the field + rule, create a project order, then poll for the flag.
await op(alva,'extensions.define-field',{entity:'order', key:'needsReview', type:'boolean',
  labels:{sv:'Granskas', en:'Review'}});
const evRule = await op(alva,'rules.define',{name:'flag-project-orders', onEvent:'order-created',
  condition: JSON.stringify({t:'bin', op:'eq', l:{t:'field', f:'orderType'}, r:{t:'const', v:'project'}}),
  messages:{}, action: JSON.stringify({type:'set-field', field:'ext.needsReview', value:true})});
check('event rule defined (onEvent, set-field)', evRule.status===200 && !!evRule.body?.output?.ruleId,
  `status=${evRule.status} ${codes(evRule).join(',')}`);
const custs = (await view(didrik,'customers.lookup','?search=')).body?.rows ?? [];
const evCustomer = custs[0]?.id;
const evProj = await op(didrik,'projects.create',{customerId: evCustomer,
  number:'P-EV-'+crypto.randomUUID().slice(0,8), name:'Effect rule project', budget: 1000});
const evOrder = await op(didrik,'orders.create',{customerId: evCustomer, orderType:'project',
  projectId: evProj.body?.output?.projectId, workAddress:'Eventgatan 1', description:'Effect-rule wire order'});
check('project order created (publishes order-created)', evOrder.status===200, `status=${evOrder.status} ${codes(evOrder).join(',')}`);
const evNumber = evOrder.body?.output?.number;
let flagged=false;
for (let i=0;i<20 && !flagged;i++){ await new Promise(r=>setTimeout(r,500));
  const rows = (await view(didrik,'orders.list','?pageSize=100')).body?.rows ?? [];
  const row = rows.find(r=>String(r.number)===String(evNumber));
  flagged = row?.extensions?.needsReview===true; }
check('effect rule set the field on dispatch', flagged, '');
const evRetire = await op(alva,'rules.retire',{name:'flag-project-orders'});
check('event rule retires', evRetire.status===200, `status=${evRetire.status}`);

console.log(results.every(Boolean) ? `ALL ${results.length} PASS` : `${results.filter(x=>!x).length}/${results.length} FAILED`);
process.exit(results.every(Boolean) ? 0 : 1);
