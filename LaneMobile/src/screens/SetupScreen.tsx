import React, { useState } from 'react';
import {
  ActivityIndicator,
  KeyboardAvoidingView,
  Platform,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { LaneClient, LaneCredentials, normaliseBaseUrl } from '../api/client';
import { DEFAULT_SESSION_KEY } from '../storage/credentials';
import { theme } from '../theme';

interface Props {
  initial: LaneCredentials | null;
  onSaved: (credentials: LaneCredentials) => void;
  onCancel?: () => void;
}

export function SetupScreen({ initial, onSaved, onCancel }: Props) {
  const [baseUrl, setBaseUrl] = useState(initial?.baseUrl ?? 'http://100.64.0.1:5080');
  const [clientKey, setClientKey] = useState(initial?.clientKey ?? '');
  const [sessionKey, setSessionKey] = useState(initial?.sessionKey ?? DEFAULT_SESSION_KEY);
  const [testing, setTesting] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);

  const save = async () => {
    setProblem(null);

    if (clientKey.trim().length === 0) {
      setProblem('A client key is required.');
      return;
    }

    const credentials: LaneCredentials = {
      baseUrl: normaliseBaseUrl(baseUrl),
      clientKey: clientKey.trim(),
      sessionKey: sessionKey.trim() || DEFAULT_SESSION_KEY,
    };

    setTesting(true);

    try {
      const client = new LaneClient(credentials);

      // Health first: it needs no key, so a failure here is the address and a failure on the
      // next call is the key. Collapsing the two makes a typo unfindable.
      await client.health();
      await client.listSessions();

      onSaved(credentials);
    } catch (error) {
      setProblem(error instanceof Error ? error.message : 'Could not connect.');
    } finally {
      setTesting(false);
    }
  };

  return (
    <KeyboardAvoidingView
      style={styles.root}
      behavior={Platform.OS === 'ios' ? 'padding' : undefined}
    >
      <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
        <Text style={styles.title}>Lane</Text>
        <Text style={styles.subtitle}>Point this at your instance.</Text>

        <Field
          label="Address"
          hint="Where the API surface listens — the tailnet address, not loopback."
          value={baseUrl}
          onChangeText={setBaseUrl}
          placeholder="http://100.x.y.z:5080"
          autoCapitalize="none"
          autoCorrect={false}
          keyboardType="url"
        />

        <Field
          label="Client key"
          hint="The key this app authenticates with, from Clients in appsettings."
          value={clientKey}
          onChangeText={setClientKey}
          placeholder="LANE_IOS_KEY"
          autoCapitalize="none"
          autoCorrect={false}
          secureTextEntry
        />

        <Field
          label="Conversation"
          hint="Your own name for this conversation. A shared: prefix lets other clients join it."
          value={sessionKey}
          onChangeText={setSessionKey}
          placeholder={DEFAULT_SESSION_KEY}
          autoCapitalize="none"
          autoCorrect={false}
        />

        {problem && <Text style={styles.problem}>{problem}</Text>}

        <Pressable
          style={({ pressed }) => [styles.primary, pressed && styles.pressed, testing && styles.primaryBusy]}
          onPress={save}
          disabled={testing}
        >
          {testing ? (
            <ActivityIndicator color={theme.background} />
          ) : (
            <Text style={styles.primaryText}>Connect</Text>
          )}
        </Pressable>

        {onCancel && (
          <Pressable style={styles.secondary} onPress={onCancel}>
            <Text style={styles.secondaryText}>Cancel</Text>
          </Pressable>
        )}
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

function Field({
  label,
  hint,
  ...input
}: { label: string; hint: string } & React.ComponentProps<typeof TextInput>) {
  return (
    <View style={styles.field}>
      <Text style={styles.label}>{label}</Text>
      <TextInput style={styles.input} placeholderTextColor={theme.textFaint} {...input} />
      <Text style={styles.hint}>{hint}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: theme.background },
  content: {
    padding: theme.spacing * 1.5,
    paddingTop: 72,
    paddingBottom: 48,
  },
  title: {
    color: theme.text,
    fontSize: 34,
    fontWeight: '700',
    letterSpacing: -0.5,
  },
  subtitle: {
    color: theme.textDim,
    fontSize: 16,
    marginTop: 6,
    marginBottom: 32,
  },
  field: { marginBottom: 22 },
  label: {
    color: theme.text,
    fontSize: 14,
    fontWeight: '600',
    marginBottom: 8,
  },
  input: {
    backgroundColor: theme.surfaceRaised,
    borderRadius: 10,
    paddingHorizontal: 14,
    paddingVertical: 13,
    color: theme.text,
    fontSize: 16,
  },
  hint: {
    color: theme.textFaint,
    fontSize: 12.5,
    marginTop: 7,
    lineHeight: 17,
  },
  problem: {
    color: theme.danger,
    fontSize: 14,
    marginBottom: 18,
    lineHeight: 20,
  },
  primary: {
    backgroundColor: theme.accent,
    borderRadius: 12,
    height: 50,
    alignItems: 'center',
    justifyContent: 'center',
    marginTop: 6,
  },
  primaryBusy: { opacity: 0.8 },
  primaryText: {
    color: theme.background,
    fontSize: 16,
    fontWeight: '700',
  },
  pressed: { opacity: 0.75 },
  secondary: {
    height: 46,
    alignItems: 'center',
    justifyContent: 'center',
    marginTop: 6,
  },
  secondaryText: {
    color: theme.textDim,
    fontSize: 15,
  },
});
