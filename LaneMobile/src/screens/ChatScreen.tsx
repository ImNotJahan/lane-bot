import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ActivityIndicator,
  FlatList,
  KeyboardAvoidingView,
  Platform,
  Pressable,
  RefreshControl,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { LaneClient, LaneCredentials } from '../api/client';
import { SseHandle } from '../api/sse';
import { MessageResponse, TurnEvent } from '../api/types';
import { ChatItem, MessageBubble } from '../components/MessageBubble';
import { Composer } from '../components/Composer';
import { theme } from '../theme';

interface Props {
  credentials: LaneCredentials;
  onOpenSettings: () => void;
}

export function ChatScreen({ credentials, onOpenSettings }: Props) {
  const client = useMemo(() => new LaneClient(credentials), [credentials]);

  const [items, setItems] = useState<ChatItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [tool, setTool] = useState<string | null>(null);
  const [banner, setBanner] = useState<string | null>(null);

  const list = useRef<FlatList<ChatItem>>(null);
  const stream = useRef<SseHandle | null>(null);
  const pendingId = useRef<string | null>(null);

  const loadHistory = useCallback(async () => {
    try {
      const messages = await client.history(100);

      setItems(messages.map(toChatItem).filter(item => item !== null) as ChatItem[]);
      setBanner(null);
    } catch (error) {
      setBanner(error instanceof Error ? error.message : 'Could not load history.');
    }
  }, [client]);

  useEffect(() => {
    let live = true;

    (async () => {
      await loadHistory();
      if (live) setLoading(false);
    })();

    return () => {
      live = false;
      stream.current?.abort();
    };
  }, [loadHistory]);

  const refresh = async () => {
    setRefreshing(true);
    await loadHistory();
    setRefreshing(false);
  };

  const patchPending = (mutate: (item: ChatItem) => ChatItem) => {
    const id = pendingId.current;

    if (!id) return;

    setItems(prev => prev.map(item => (item.id === id ? mutate(item) : item)));
  };

  const handleEvent = (event: TurnEvent) => {
    switch (event.type) {
      case 'delta':
        patchPending(item => ({ ...item, text: item.text + event.data.text }));
        break;

      case 'tool':
        setTool(event.data.phase === 'start' ? event.data.name : null);
        break;

      case 'message': {
        // The authoritative text, which may differ from the deltas after post-processing.
        patchPending(item => ({ ...item, text: event.data.text, pending: false }));
        pendingId.current = null;
        break;
      }

      case 'done':
        settle(event.data.silent);
        break;

      case 'error':
        settle(false, event.data.error);
        break;
    }
  };

  /** Resolves whatever bubble is still streaming, however the turn ended. */
  const settle = (silent: boolean, error?: string) => {
    const id = pendingId.current;

    if (id) {
      setItems(prev =>
        prev.flatMap(item => {
          if (item.id !== id) return [item];

          if (error) return [{ ...item, text: error, pending: false, failed: true }];

          // An empty bubble means she chose not to answer — the response-policy gate doing
          // its job. Saying so is kinder than a message that vanishes into nothing.
          if (item.text.length === 0) {
            return silent
              ? [{ ...item, role: 'note' as const, text: 'She let that one pass.', pending: false }]
              : [];
          }

          return [{ ...item, pending: false }];
        }),
      );

      pendingId.current = null;
    }

    setTool(null);
    setBusy(false);
  };

  const send = (text: string) => {
    const now = new Date().toISOString();
    const id = `pending-${Date.now()}`;

    pendingId.current = id;

    setItems(prev => [
      ...prev,
      { id: `local-${Date.now()}`, role: 'user', author: 'you', text, timestamp: now },
      { id, role: 'lane', author: 'Lane', text: '', timestamp: now, pending: true },
    ]);

    setBusy(true);
    setBanner(null);

    stream.current = client.send(
      text,
      handleEvent,
      error => settle(false, error.message),
      () => {
        stream.current = null;
        // A stream that closed without a done frame still has to release the composer.
        if (pendingId.current) settle(false);
        else setBusy(false);
      },
    );
  };

  const cancel = async () => {
    try {
      await client.cancel();
    } catch {
      // Stopping the local stream still matters even if the request did not land.
    }

    stream.current?.abort();
    stream.current = null;
    settle(false);
  };

  if (loading) {
    return (
      <View style={[styles.root, styles.centred]}>
        <ActivityIndicator color={theme.accent} />
      </View>
    );
  }

  return (
    <SafeAreaView style={styles.root} edges={['top']}>
      <View style={styles.header}>
        <View>
          <Text style={styles.headerTitle}>Lane</Text>
          <Text style={styles.headerSubtitle}>{credentials.sessionKey}</Text>
        </View>

        <Pressable onPress={onOpenSettings} hitSlop={12}>
          <Text style={styles.headerAction}>Settings</Text>
        </Pressable>
      </View>

      {banner && (
        <Pressable style={styles.banner} onPress={() => setBanner(null)}>
          <Text style={styles.bannerText}>{banner}</Text>
        </Pressable>
      )}

      <KeyboardAvoidingView
        style={styles.flex}
        behavior={Platform.OS === 'ios' ? 'padding' : undefined}
        keyboardVerticalOffset={Platform.OS === 'ios' ? 8 : 0}
      >
        <FlatList
          ref={list}
          data={items}
          keyExtractor={item => item.id}
          renderItem={({ item }) => <MessageBubble item={item} />}
          contentContainerStyle={styles.listContent}
          onContentSizeChange={() => list.current?.scrollToEnd({ animated: true })}
          refreshControl={
            <RefreshControl refreshing={refreshing} onRefresh={refresh} tintColor={theme.textDim} />
          }
          ListEmptyComponent={
            <Text style={styles.empty}>Nothing here yet. Say something.</Text>
          }
        />

        {tool && (
          <View style={styles.toolChip}>
            <Text style={styles.toolText}>{describeTool(tool)}</Text>
          </View>
        )}

        <Composer busy={busy} onSend={send} onCancel={cancel} />
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

/**
 * Transcript rows that are not conversation. Tool traffic carries no text, thoughts belong
 * to no conversation, and summaries are memory rather than something anybody said.
 */
function toChatItem(message: MessageResponse): ChatItem | null {
  if (message.kind !== 'Utterance') return null;
  if (message.text.trim().length === 0) return null;
  if (message.role !== 'User' && message.role !== 'Assistant') return null;

  return {
    id: message.id,
    role: message.role === 'Assistant' ? 'lane' : 'user',
    author: message.author,
    text: message.text,
    timestamp: message.timestamp,
  };
}

function describeTool(name: string): string {
  switch (name) {
    case 'web_search':
      return 'searching the web';
    case 'fetch_url':
      return 'reading a page';
    case 'read_book':
      return 'reading';
    case 'write_note':
    case 'read_note':
      return 'checking her notes';
    default:
      return name.replace(/_/g, ' ');
  }
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: theme.background },
  flex: { flex: 1 },
  centred: { alignItems: 'center', justifyContent: 'center' },
  header: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingHorizontal: theme.spacing,
    paddingVertical: 12,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: theme.border,
  },
  headerTitle: {
    color: theme.text,
    fontSize: 20,
    fontWeight: '700',
  },
  headerSubtitle: {
    color: theme.textFaint,
    fontSize: 12,
    marginTop: 2,
  },
  headerAction: {
    color: theme.accent,
    fontSize: 15,
  },
  banner: {
    backgroundColor: '#3a2226',
    paddingHorizontal: theme.spacing,
    paddingVertical: 10,
  },
  bannerText: {
    color: theme.danger,
    fontSize: 13.5,
    lineHeight: 19,
  },
  listContent: {
    paddingVertical: 12,
    flexGrow: 1,
  },
  empty: {
    color: theme.textFaint,
    textAlign: 'center',
    marginTop: 64,
    fontSize: 15,
  },
  toolChip: {
    alignSelf: 'flex-start',
    marginLeft: theme.spacing,
    marginBottom: 6,
    paddingHorizontal: 12,
    paddingVertical: 6,
    borderRadius: 12,
    backgroundColor: theme.surfaceRaised,
  },
  toolText: {
    color: theme.textDim,
    fontSize: 12.5,
    fontStyle: 'italic',
  },
});
