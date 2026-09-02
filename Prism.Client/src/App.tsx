import { useEffect, useState } from "react";
import { Route, Routes } from "react-router";
import {
  ActionIcon,
  AppShell,
  Button,
  Container,
  Group,
  Modal,
  Stack,
  TextInput,
  Title,
} from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { useServerUrl } from "./serverUrl";
import { Library } from "./components/Library";
import { Watch } from "./components/Watch";
import { DebugPanel } from "./components/DebugPanel";

export function App() {
  return (
    <AppShell header={{ height: 56 }} padding="md">
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between">
          <Title order={3}>Prism</Title>
          <ServerUrlSettings />
        </Group>
      </AppShell.Header>
      <AppShell.Main>
        <Container size="lg" px={0}>
          <Routes>
            <Route path="/" element={<Library />} />
            <Route path="/watch/:id" element={<Watch />} />
            <Route path="/debug" element={<DebugPanel />} />
          </Routes>
        </Container>
      </AppShell.Main>
    </AppShell>
  );
}

// Статуса сервера в шапке нет намеренно: клиент говорит с библиотекой, а
// ffmpeg и кодек — свойства конкретного хоста, которых у неё несколько. Эти
// сведения и дебаг сессий показываются на странице фильма, где хост известен.
function ServerUrlSettings() {
  const { serverUrl, setServerUrl } = useServerUrl();
  const [opened, { open, close }] = useDisclosure(false);
  const [draft, setDraft] = useState(serverUrl);

  useEffect(() => setDraft(serverUrl), [serverUrl]);

  return (
    <>
      <ActionIcon variant="default" size="lg" onClick={open} aria-label="Настройки">
        ⚙
      </ActionIcon>
      <Modal opened={opened} onClose={close} title="Настройки клиента" centered>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            setServerUrl(draft);
            close();
          }}
        >
          <Stack gap="xs">
            <TextInput
              label="URL библиотеки"
              description="По умолчанию — адрес, с которого открыт клиент"
              placeholder="http://localhost:8081"
              value={draft}
              spellCheck={false}
              onChange={(e) => setDraft(e.currentTarget.value)}
            />
            <Button type="submit">Сохранить</Button>
          </Stack>
        </form>
      </Modal>
    </>
  );
}
