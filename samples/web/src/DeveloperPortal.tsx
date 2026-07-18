import { Accordion, Badge, Card, Code, Group, SimpleGrid, Stack, Text, Title } from '@mantine/core';
import { useTam, useView } from '@tam/react';

/**
 * The developer portal (docs/31 slice 3): the host's extension surface rendered in the app —
 * the SAME contract that ships as host-contract.json and as the generated HostContract
 * symbols, served through the ordinary `developer.contract` view (permission-checked like
 * everything else). What a plugin author browses before writing a line.
 */
type Contract = {
  events: Record<string, { fields: string[]; kinds: Record<string, string> }>;
  views: Record<string, { permission: string; fields: string[]; kinds: Record<string, string> }>;
  slots: Record<string, { keys: string[] }>;
  extensibleEntities: string[];
  operations: string[];
};

function FieldBadges({ fields, kinds }: { fields: string[]; kinds: Record<string, string> }) {
  return (
    <Group gap={6}>
      {fields.map((field) => (
        <Badge key={field} variant="light" radius="sm" tt="none" fw={500}>
          {field}
          {kinds[field] ? <Text span c="dimmed" size="xs">{` : ${kinds[field]}`}</Text> : null}
        </Badge>
      ))}
    </Group>
  );
}

export function DeveloperPortal() {
  const { t } = useTam();
  const contractRows = useView('developer.contract', {});
  const raw = (contractRows.data?.rows?.[0] as { contract?: string } | undefined)?.contract;
  if (!raw) return null;
  const contract = JSON.parse(raw) as Contract;

  return (
    <Stack gap="lg">
      <Stack gap={4}>
        <Title order={3}>{t('nav.developer')}</Title>
        <Text c="dimmed" size="sm">{t('dev.intro')}</Text>
      </Stack>

      <Stack gap={6}>
        <Title order={4}>{t('dev.headings.events')}</Title>
        <Text c="dimmed" size="xs">{t('dev.hint.events')}</Text>
        <SimpleGrid cols={{ base: 1, md: 2 }}>
          {Object.entries(contract.events).map(([id, event]) => (
            <Card key={id} withBorder padding="sm">
              <Stack gap={8}>
                <Code fw={600}>{id}</Code>
                <FieldBadges fields={event.fields} kinds={event.kinds} />
              </Stack>
            </Card>
          ))}
        </SimpleGrid>
      </Stack>

      <Stack gap={6}>
        <Title order={4}>{t('dev.headings.views')}</Title>
        <Text c="dimmed" size="xs">{t('dev.hint.views')}</Text>
        <Accordion variant="contained" multiple>
          {Object.entries(contract.views).map(([id, view]) => (
            <Accordion.Item key={id} value={id}>
              <Accordion.Control>
                <Group gap="sm">
                  <Code fw={600}>{id}</Code>
                  <Badge size="xs" variant="outline" tt="none">{view.permission}</Badge>
                </Group>
              </Accordion.Control>
              <Accordion.Panel>
                <FieldBadges fields={view.fields} kinds={view.kinds} />
              </Accordion.Panel>
            </Accordion.Item>
          ))}
        </Accordion>
      </Stack>

      <SimpleGrid cols={{ base: 1, md: 2 }}>
        <Stack gap={6}>
          <Title order={4}>{t('dev.headings.slots')}</Title>
          <Text c="dimmed" size="xs">{t('dev.hint.slots')}</Text>
          {Object.entries(contract.slots).map(([id, slot]) => (
            <Card key={id} withBorder padding="sm">
              <Group gap="sm">
                <Code fw={600}>{id}</Code>
                {slot.keys.map((key) => <Badge key={key} size="xs" variant="light" tt="none">{key}</Badge>)}
              </Group>
            </Card>
          ))}
        </Stack>
        <Stack gap={6}>
          <Title order={4}>{t('dev.headings.entities')}</Title>
          <Text c="dimmed" size="xs">{t('dev.hint.entities')}</Text>
          <Group gap={6}>
            {contract.extensibleEntities.map((entity) => (
              <Badge key={entity} variant="filled" radius="sm" tt="none">{entity}</Badge>
            ))}
          </Group>
        </Stack>
      </SimpleGrid>

      <Stack gap={6}>
        <Title order={4}>{`${t('dev.headings.operations')} (${contract.operations.length})`}</Title>
        <Text c="dimmed" size="xs">{t('dev.hint.operations')}</Text>
        <Group gap={6}>
          {contract.operations.map((operation) => (
            <Code key={operation}>{operation}</Code>
          ))}
        </Group>
      </Stack>
    </Stack>
  );
}
