import { useEffect, useMemo, useState, type ReactNode } from 'react';
import {
  AppShell, Button, Center, Group, Loader, Modal, NavLink, SegmentedControl, Select, Stack, Text,
  TextInput, Title,
} from '@mantine/core';
import { TamClient, type StandableInfo } from '@tam/core';
import {
  FieldRendererProps, LookupSelect, NavModeSwitcher, NavPage, NavProvider, NavSidebar, NavTabs,
  OperationForm, TamProvider, ViewGrid, registerBadgeColors, registerPage, registerRenderer,
  useTam, useTamAuth,
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

registerRenderer('customer-picker', CustomerPicker);
// Domain enum colors for grid badges — the framework ships only its own registry states.
registerBadgeColors({
  open: 'blue', completed: 'green', cancelled: 'gray', project: 'grape', service: 'cyan',
});

// ---- Pages: each is a grid + modals, everything else comes from the manifest ----

function OrdersPage() {
  const { client, t, refreshManifest, can } = useTam();
  const [editing, setEditing] = useState<Record<string, unknown> | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);

  const openEdit = async (row: Record<string, unknown>) => {
    if (!can('orders.edit')) return;
    // A subtree grid may hand us a child company's row: read + edit in the ROW's node.
    const actAs = typeof row.tenantId === 'string' ? row.tenantId : undefined;
    const detail = await client.view('orders.detail', { orderId: row.id }, actAs ? { actAs } : undefined);
    const detailRow = detail.rows[0] ?? null;
    setEditing(detailRow ? { ...detailRow, tenantId: row.tenantId } : null);
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
            actAs={typeof editing.tenantId === 'string' ? editing.tenantId : undefined}
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

// The one genuinely custom page: row-click loads the detail view and opens the edit form with
// initial values — app logic, not derivable from the manifest. Registered under the key the
// host's nav declaration binds ({ page: "orders" }).
registerPage('orders', () => <OrdersPage />);

function Shell(props: {
  userName: string;
  onLogout: () => void;
  standable: StandableInfo | null;
  actAs: string | null;
  onActAs: (id: string | null) => void;
}) {
  const { t, culture, setCulture } = useTam();

  // The acting-company selector (docs/26 D-H4): every standable node — memberships plus cascaded
  // descendants, labeled by path. Picking one rebinds every request via the act-as header (the
  // server validates it), so views, forms, lookups and creates all act in that node — no re-login.
  const companies = props.standable;

  // The nav itself is the manifest's (docs/30): modes → switcher, depth 1 → sidebar,
  // depth 2 → tabs, pages → generic grid/plugin renders or registered custom pages.
  return (
    <NavProvider>
      <AppShell header={{ height: 56 }} navbar={{ width: 220, breakpoint: 'sm' }} padding="lg">
        <AppShell.Header>
          <Group h="100%" px="md" justify="space-between">
            <Group gap="xs">
              <Text fw={700} size="lg" c="indigo">◆</Text>
              <Title order={4}>{t('app.title')}</Title>
              <NavModeSwitcher />
              {companies && companies.nodes.length > 1 && (
                <Select
                  size="xs"
                  w={240}
                  ml="md"
                  value={props.actAs ?? companies.active}
                  data={companies.nodes.map(n => ({ value: n.id, label: n.display }))}
                  onChange={v => props.onActAs(v === null || v === companies.active ? null : v)}
                  allowDeselect={false}
                />
              )}
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
          <NavSidebar />
        </AppShell.Navbar>
        <AppShell.Main>
          <Stack gap="md">
            <NavTabs />
            <NavPage />
          </Stack>
        </AppShell.Main>
      </AppShell>
    </NavProvider>
  );
}

function LoginPage(p: { onSignIn: () => void }) {
  const { t } = useTam();
  // The credential form and tenant picker are the framework's (server-rendered, localized); this page
  // only offers to start the redirect. PKCE lives in useTamAuth, not here.
  return (
    <Group justify="center" pt={120}>
      <Stack w={320} gap="md" align="center">
        <Group gap="xs" justify="center">
          <Text fw={700} size="lg" c="indigo">◆</Text>
          <Title order={3}>{t('app.title')}</Title>
        </Group>
        <Button fullWidth onClick={p.onSignIn}>{t('auth.sign-in')}</Button>
      </Stack>
    </Group>
  );
}

export function App() {
  // All the auth mechanics (PKCE redirect, /callback code exchange, token storage, bearer wiring)
  // live in the framework hook — the app just reacts to the session state.
  const auth = useTamAuth(client, { clientId: 'tam-spa' });
  const [standable, setStandable] = useState<StandableInfo | null>(null);
  const [actAs, setActAs] = useState<string | null>(null);

  // The account's standable companies (docs/26 D-H3): memberships + cascaded descendants.
  useEffect(() => {
    if (auth.status !== 'authenticated') { setStandable(null); setActAs(null); return; }
    client.standable()
      .then(d => setStandable(d))
      .catch(() => setStandable(null));
  }, [auth.status]);

  // Acting company (docs/26 D-H4): the act-as header rebinds every request server-side after
  // validation — views, lookups and creates all land in the chosen node, no re-login.
  client.actAs(actAs);

  if (auth.status === 'loading') return <Center pt={160}><Loader /></Center>;

  // Remount the provider on identity or acting-company change: either means a new effective manifest.
  return (
    <TamProvider
      key={`${auth.user?.sub ?? 'anonymous'}:${actAs ?? ''}`}
      client={client}
      initialCulture="sv"
    >
      {auth.status === 'authenticated'
        ? <Shell userName={auth.user!.name} onLogout={auth.signOut}
            standable={standable} actAs={actAs} onActAs={setActAs} />
        : <LoginPage onSignIn={auth.signIn} />}
    </TamProvider>
  );
}
