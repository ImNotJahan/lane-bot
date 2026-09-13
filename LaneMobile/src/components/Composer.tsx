import React, { useState } from 'react';
import { Pressable, StyleSheet, Text, TextInput, View } from 'react-native';
import { theme } from '../theme';

interface Props {
  busy: boolean;
  onSend: (text: string) => void;
  onCancel: () => void;
}

export function Composer({ busy, onSend, onCancel }: Props) {
  const [draft, setDraft] = useState('');

  const submit = () => {
    const text = draft.trim();

    if (text.length === 0 || busy) return;

    setDraft('');
    onSend(text);
  };

  return (
    <View style={styles.bar}>
      <TextInput
        style={styles.input}
        value={draft}
        onChangeText={setDraft}
        placeholder="Say something"
        placeholderTextColor={theme.textFaint}
        multiline
        onSubmitEditing={submit}
        blurOnSubmit={false}
        returnKeyType="send"
      />

      <Pressable
        style={({ pressed }) => [
          styles.button,
          busy ? styles.buttonCancel : styles.buttonSend,
          pressed && styles.pressed,
          !busy && draft.trim().length === 0 && styles.buttonDisabled,
        ]}
        onPress={busy ? onCancel : submit}
        disabled={!busy && draft.trim().length === 0}
      >
        <Text style={[styles.buttonText, busy && styles.buttonTextCancel]}>{busy ? 'Stop' : 'Send'}</Text>
      </Pressable>
    </View>
  );
}

const styles = StyleSheet.create({
  bar: {
    flexDirection: 'row',
    alignItems: 'flex-end',
    paddingHorizontal: theme.spacing,
    paddingTop: 10,
    paddingBottom: 10,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: theme.border,
    backgroundColor: theme.surface,
    gap: 10,
  },
  input: {
    flex: 1,
    maxHeight: 132,
    minHeight: 42,
    paddingHorizontal: 14,
    paddingTop: 11,
    paddingBottom: 11,
    borderRadius: 21,
    backgroundColor: theme.surfaceRaised,
    color: theme.text,
    fontSize: 16,
  },
  button: {
    height: 42,
    paddingHorizontal: 18,
    borderRadius: 21,
    alignItems: 'center',
    justifyContent: 'center',
  },
  buttonSend: { backgroundColor: theme.accent },
  buttonCancel: { backgroundColor: 'transparent', borderWidth: 1, borderColor: theme.danger },
  buttonDisabled: { backgroundColor: theme.border },
  pressed: { opacity: 0.7 },
  buttonText: {
    color: theme.background,
    fontSize: 15,
    fontWeight: '600',
  },
  buttonTextCancel: { color: theme.danger },
});
