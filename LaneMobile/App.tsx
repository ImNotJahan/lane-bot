import React, { useEffect, useState } from 'react';
import { ActivityIndicator, StatusBar, StyleSheet, View } from 'react-native';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { LaneCredentials } from './src/api/client';
import { loadCredentials, saveCredentials } from './src/storage/credentials';
import { ChatScreen } from './src/screens/ChatScreen';
import { SetupScreen } from './src/screens/SetupScreen';
import { VoiceScreen } from './src/screens/VoiceScreen';
import { Tab, TabBar } from './src/components/TabBar';
import { theme } from './src/theme';

export default function App() {
  const [credentials, setCredentials] = useState<LaneCredentials | null>(null);
  const [editing, setEditing] = useState(false);
  const [loading, setLoading] = useState(true);
  const [tab, setTab] = useState<Tab>('chat');

  useEffect(() => {
    (async () => {
      setCredentials(await loadCredentials());
      setLoading(false);
    })();
  }, []);

  const save = async (next: LaneCredentials) => {
    await saveCredentials(next);
    setCredentials(next);
    setEditing(false);
  };

  const body = () => {
    if (loading) {
      return (
        <View style={styles.centred}>
          <ActivityIndicator color={theme.accent} />
        </View>
      );
    }

    if (!credentials || editing) {
      return (
        <SetupScreen
          initial={credentials}
          onSaved={save}
          onCancel={credentials ? () => setEditing(false) : undefined}
        />
      );
    }

    // Only the active tab is mounted. That is what stops the microphone when the voice tab
    // is left: its cleanup runs, rather than a session staying live behind the chat.
    return (
      <>
        <View style={styles.flex}>
          {tab === 'chat' ? (
            <ChatScreen credentials={credentials} onOpenSettings={() => setEditing(true)} />
          ) : (
            <VoiceScreen credentials={credentials} />
          )}
        </View>

        <TabBar active={tab} onChange={setTab} />
      </>
    );
  };

  return (
    <SafeAreaProvider style={styles.root}>
      <StatusBar barStyle="light-content" />
      {body()}
    </SafeAreaProvider>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: theme.background },
  flex: { flex: 1 },
  centred: { flex: 1, alignItems: 'center', justifyContent: 'center' },
});
