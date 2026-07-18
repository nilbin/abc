import { useState } from 'react';
import {
  ActionIcon, Alert, Button, Group, Modal, NavLink, Paper, Stack, Table, Text, Title,
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
  const [dialog, setDialog] = useState<'folder' | 'upload' | null>(null);
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
    </Stack>
  );
}
