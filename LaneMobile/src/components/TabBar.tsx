import React from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { theme } from '../theme';

export type Tab = 'chat' | 'voice';

interface Props {
  active: Tab;
  onChange: (tab: Tab) => void;
}

export function TabBar({ active, onChange }: Props) {
  const insets = useSafeAreaInsets();

  return (
    <View style={[styles.bar, { paddingBottom: Math.max(insets.bottom, 10) }]}>
      <TabButton label="Chat" tab="chat" active={active} onChange={onChange} />
      <TabButton label="Voice" tab="voice" active={active} onChange={onChange} />
    </View>
  );
}

function TabButton({
  label,
  tab,
  active,
  onChange,
}: {
  label: string;
  tab: Tab;
  active: Tab;
  onChange: (tab: Tab) => void;
}) {
  const selected = active === tab;

  return (
    <Pressable
      style={({ pressed }) => [styles.tab, pressed && styles.pressed]}
      onPress={() => onChange(tab)}
    >
      <Text style={[styles.label, selected && styles.labelActive]}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  bar: {
    flexDirection: 'row',
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: theme.border,
    backgroundColor: theme.surface,
  },
  tab: { flex: 1, alignItems: 'center', paddingTop: 10, paddingBottom: 6 },
  label: { color: theme.textFaint, fontSize: 14, fontWeight: '600' },
  labelActive: { color: theme.accent },
  pressed: { opacity: 0.6 },
});
