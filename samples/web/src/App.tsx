import { DocumentsBrowser } from './DocumentsBrowser';
import { DeveloperPortal } from './DeveloperPortal';
import { useEffect, useState, type ReactNode } from 'react';
import {
  AppShell, Button, Center, Group, Loader, SegmentedControl, Select, Stack, Text,
  TextInput, Title,
} from '@mantine/core';
import { TamClient, type StandableInfo } from '@tam/core';
import {
  NavModeSwitcher, NavPage, NavProvider, NavSidebar, NavTabs,
  TamProvider, readQuery, registerBadgeColors,
  registerPage, useTam, useTamAuth, writeQuery,
} from '@tam/react';

const client = new TamClient(import.meta.env.VITE_API ?? '', 'sv');

// ---- App-owned look & feel: the framework owns semantics, the app owns pixels (docs/13).
// The old CustomerPicker renderer is GONE — [Lookup("customers.lookup")] on the CustomerId
// wrapper renders the picker from the manifest (docs/02); registerRenderer remains the seat
// for genuinely bespoke controls.

// Domain enum colors for grid badges — the framework ships only its own registry states.
registerBadgeColors({
  open: 'blue', completed: 'green', cancelled: 'gray', project: 'grape', service: 'cyan',
});

// ---- Pages: model-declared pages render through the framework's ModelPage (docs/32).
// TWO registered pages — both genuinely custom UX, the escape hatch used as intended
// (the registerPage ratio is the architecture tripwire): the documents tree browser and
// the developer portal (docs/31 slice 3 — the extension surface rendered in the app).
registerPage('documents-browser', () => <DocumentsBrowser />);
registerPage('developer-portal', () => <DeveloperPortal />);

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
  // The acting company survives reload and travels in links: ?tenant seeds it (the server
  // validates the act-as header on every request regardless — the URL is a wish, not a grant).
  const [actAs, setActAs] = useState<string | null>(() => readQuery().tenant);

  // The account's standable companies (docs/26 D-H3): memberships + cascaded descendants.
  // A ?tenant outside the standable set is dropped — a stale or foreign link degrades to the
  // home node instead of a wall of failed requests.
  useEffect(() => {
    if (auth.status !== 'authenticated') { setStandable(null); setActAs(null); return; }
    client.standable()
      .then(d => {
        setStandable(d);
        setActAs(current => {
          if (current && !d.nodes.some(n => n.id === current)) {
            writeQuery({ tenant: null });
            return null;
          }
          return current;
        });
      })
      .catch(() => setStandable(null));
  }, [auth.status]);

  // Back/forward across acting-company changes: the URL's ?tenant drives the scope.
  useEffect(() => {
    const onPop = () => setActAs(readQuery().tenant);
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, []);

  const onActAs = (id: string | null) => {
    setActAs(id);
    // A record from the previous scope is meaningless in the new one.
    writeQuery({ tenant: id, record: null, tab: null });
  };

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
            standable={standable} actAs={actAs} onActAs={onActAs} />
        : <LoginPage onSignIn={auth.signIn} />}
    </TamProvider>
  );
}
