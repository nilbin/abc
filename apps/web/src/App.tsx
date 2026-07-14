import { useMemo, useState, type ReactNode } from 'react';
import {
  AppShell, Button, Group, Modal, NavLink, SegmentedControl, Stack, Text, TextInput, Title,
} from '@mantine/core';
import { TamClient } from '@tam/core';
import {
  FieldRendererProps, LookupSelect, OperationForm, TamProvider, ViewGrid, registerRenderer, useTam,
} from '@tam/react';

const client = new TamClient(import.meta.env.VITE_API ?? '', 'sv');

// ---- App-owned renderers: the framework owns semantics, the app owns pixels (docs/13) ----

function CustomerPicker(p: FieldRendererProps) {
  return (
    <LookupSelect
      view="customers.lookup"
      label={p.label}
      required={p.required}
      error={p.error}
      description={p.warning}
      value={p.value}
      onChange={v => p.onChange(v)}
    />
  );
}

function CultureText(p: FieldRendererProps) {
  const value = (p.value ?? {}) as Record<string, string>;
  const set = (culture: string, text: string) =>
    p.onChange({ ...value, [culture]: text || undefined });
  return (
    <Stack gap={4}>
      <Text size="sm" fw={500}>{p.label}{p.required ? ' *' : ''}</Text>
      <Group grow>
        <TextInput placeholder="Svenska" value={value.sv ?? ''}
          onChange={e => set('sv', e.currentTarget.value)} error={p.error} />
        <TextInput placeholder="English" value={value.en ?? ''}
          onChange={e => set('en', e.currentTarget.value)} />
      </Group>
    </Stack>
  );
}

registerRenderer('customer-picker', CustomerPicker);
registerRenderer('culture-text', CultureText);

// ---- Pages: each is a grid + modals, everything else comes from the manifest ----

function OrdersPage() {
  const { client, t, refreshManifest, can } = useTam();
  const [editing, setEditing] = useState<Record<string, unknown> | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);

  const openEdit = async (row: Record<string, unknown>) => {
    if (!can('orders.edit')) return;
    const detail = await client.view('orders.detail', { orderId: row.id });
    setEditing(detail.rows[0] ?? null);
  };

  return (
    <>
      <ViewGrid
        grid="web.orders.list"
        onRowClick={row => void openEdit(row)}
        refreshKey={refreshKey}
        onAction={() => void refreshManifest()}
      />
      <Modal
        opened={editing !== null}
        onClose={() => setEditing(null)}
        title={<Title order={4}>{t('operations.orders.edit-details.title')} — {String(editing?.number ?? '')}</Title>}
        size="lg"
      >
        {editing && (
          <OperationForm
            form="web.orders.edit"
            initialValues={{
              orderId: editing.id,
              description: editing.description,
              requestedDate: editing.requestedDate,
              workAddress: editing.workAddress,
              estimatedTotal: editing.estimatedTotal,
            }}
            initialExtensions={(editing.extensions as Record<string, unknown>) ?? {}}
            onSuccess={() => { setEditing(null); setRefreshKey(k => k + 1); }}
          />
        )}
      </Modal>
    </>
  );
}

function CustomersPage() {
  return <ViewGrid grid="web.customers.list" />;
}

function AuditPage() {
  return <ViewGrid grid="web.audit.list" pageSize={20} />;
}

function ExtensionsPage() {
  const { refreshManifest } = useTam();
  return <ViewGrid grid="web.extensions.fields" onAction={() => void refreshManifest()} />;
}

function PluginsPage() {
  const { refreshManifest } = useTam();
  // Activation flips the effective manifest (nav, grids, MCP tools) — refresh after actions.
  return <ViewGrid grid="web.plugins" onAction={() => void refreshManifest()} />;
}

function PackagesPage() {
  const { refreshManifest } = useTam();
  return <ViewGrid grid="web.packages" onAction={() => void refreshManifest()} />;
}

function RulesPage() {
  return <ViewGrid grid="web.rules" />;
}

/** Generic page for an ACTIVE plugin: renders every grid the plugin contributed.
 *  Nothing here knows what "inspect" is — the manifest is the only source. */
function PluginPage(props: { plugin: string }) {
  const { manifest } = useTam();
  const grids = Object.entries(manifest.grids)
    .filter(([, g]) => g.plugin === props.plugin)
    .map(([id]) => id);
  return (
    <Stack gap="lg">
      {grids.map(id => <ViewGrid key={id} grid={id} />)}
    </Stack>
  );
}

function Shell(props: { userName: string; onLogout: () => void }) {
  const { t, culture, setCulture, can, manifest } = useTam();
  const [page, setPage] = useState<string>('orders');

  // Active plugins contribute nav entries straight from the manifest (docs/22): activation
  // per tenant is the whole install experience — no app code names any plugin.
  const activePlugins = manifest.plugins ?? [];

  const pages = useMemo(() => ({
    orders: <OrdersPage />,
    customers: <CustomersPage />,
    extensions: <ExtensionsPage />,
    audit: <AuditPage />,
    plugins: <PluginsPage />,
    packages: <PackagesPage />,
    rules: <RulesPage />,
    ...Object.fromEntries(activePlugins.map(id =>
      [`plugin:${id}`, <PluginPage key={id} plugin={id} />])),
  }) as Record<string, ReactNode>, [activePlugins.join(',')]);

  const current = pages[page] ?? pages.orders;

  return (
    <AppShell header={{ height: 56 }} navbar={{ width: 220, breakpoint: 'sm' }} padding="lg">
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between">
          <Group gap="xs">
            <Text fw={700} size="lg" c="indigo">◆</Text>
            <Title order={4}>{t('app.title')}</Title>
          </Group>
          <Group gap="sm">
            <Text size="sm" c="dimmed">{props.userName}</Text>
            <Button size="compact-xs" variant="subtle" onClick={props.onLogout}>
              {t('auth.sign-out')}
            </Button>
            <SegmentedControl
              size="xs"
              value={culture}
              onChange={v => setCulture(v)}
              data={[{ value: 'sv', label: 'Svenska' }, { value: 'en', label: 'English' }]}
            />
          </Group>
        </Group>
      </AppShell.Header>
      <AppShell.Navbar p="xs">
        <NavLink label={t('nav.orders')} active={page === 'orders'} onClick={() => setPage('orders')} />
        <NavLink label={t('nav.customers')} active={page === 'customers'} onClick={() => setPage('customers')} />
        {activePlugins.filter(id =>
          Object.values(manifest.grids).some(g =>
            g.plugin === id && can(manifest.views[g.view]?.permission ?? ''))
        ).map(id => (
          <NavLink key={id} label={t(`plugins.${id}.title`)}
            active={page === `plugin:${id}`} onClick={() => setPage(`plugin:${id}`)} />
        ))}
        {can('extensions.manage') && (
          <NavLink label={t('nav.extensions')} active={page === 'extensions'} onClick={() => setPage('extensions')} />
        )}
        {can('plugins.manage') && (
          <NavLink label={t('nav.plugins')} active={page === 'plugins'} onClick={() => setPage('plugins')} />
        )}
        {can('packages.manage') && (
          <NavLink label={t('nav.packages')} active={page === 'packages'} onClick={() => setPage('packages')} />
        )}
        {can('rules.manage') && (
          <NavLink label={t('nav.rules')} active={page === 'rules'} onClick={() => setPage('rules')} />
        )}
        {can('audit.read') && (
          <NavLink label={t('nav.audit')} active={page === 'audit'} onClick={() => setPage('audit')} />
        )}
      </AppShell.Navbar>
      <AppShell.Main>{current}</AppShell.Main>
    </AppShell>
  );
}

function LoginPage(p: { onLogin: (token: string, user: string) => void }) {
  const { t } = useTam();
  const [user, setUser] = useState('alva');
  const [password, setPassword] = useState('demo123');
  const [error, setError] = useState<string | null>(null);

  const submit = async (u: string, pw: string) => {
    setError(null);
    const response = await fetch(`${client.baseUrl}/connect/token`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ grant_type: 'password', username: u, password: pw }),
    });
    const data = await response.json().catch(() => ({}));
    if (!response.ok || !data.access_token) { setError(t('auth.failed')); return; }
    p.onLogin(data.access_token, u);
  };

  return (
    <Group justify="center" pt={120}>
      <Stack w={340} gap="sm">
        <Group gap="xs" justify="center">
          <Text fw={700} size="lg" c="indigo">◆</Text>
          <Title order={3}>{t('app.title')}</Title>
        </Group>
        <TextInput label={t('labels.user-name')} value={user}
          onChange={e => setUser(e.currentTarget.value)} />
        <TextInput label={t('labels.password')} type="password" value={password} error={error}
          onChange={e => setPassword(e.currentTarget.value)}
          onKeyDown={e => { if (e.key === 'Enter') void submit(user, password); }} />
        <Button onClick={() => void submit(user, password)}>{t('auth.sign-in')}</Button>
        <Group gap={6} justify="center">
          {['alva', 'didrik', 'tekla', 'vera'].map(u => (
            <Button key={u} size="compact-xs" variant="light"
              onClick={() => void submit(u, 'demo123')}>{u}</Button>
          ))}
        </Group>
      </Stack>
    </Group>
  );
}

export function App() {
  const [token, setToken] = useState<string | null>(sessionStorage.getItem('tam-token'));
  const [userName, setUserName] = useState<string | null>(sessionStorage.getItem('tam-user'));

  if (token) client.headers['Authorization'] = `Bearer ${token}`;
  else delete client.headers['Authorization'];

  const login = (nextToken: string, user: string) => {
    sessionStorage.setItem('tam-token', nextToken);
    sessionStorage.setItem('tam-user', user);
    setToken(nextToken);
    setUserName(user);
  };
  const logout = () => {
    sessionStorage.removeItem('tam-token');
    sessionStorage.removeItem('tam-user');
    setToken(null);
    setUserName(null);
  };

  // Remount the provider on identity change: new actor → new effective manifest.
  return (
    <TamProvider key={userName ?? 'anonymous'} client={client} initialCulture="sv">
      {token && userName ? <Shell userName={userName} onLogout={logout} /> : <LoginPage onLogin={login} />}
    </TamProvider>
  );
}
