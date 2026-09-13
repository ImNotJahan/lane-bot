import React from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { theme } from '../theme';

export interface ChatItem {
  id: string;
  role: 'user' | 'lane' | 'note';
  author: string;
  text: string;
  timestamp: string;
  pending?: boolean;
  failed?: boolean;
}

export function MessageBubble({ item }: { item: ChatItem }) {
  if (item.role === 'note') {
    return (
      <View style={styles.noteRow}>
        <Text style={styles.note}>{item.text}</Text>
      </View>
    );
  }

  const mine = item.role === 'user';

  return (
    <View style={[styles.row, mine ? styles.rowMine : styles.rowTheirs]}>
      <View
        style={[
          styles.bubble,
          mine ? styles.bubbleMine : styles.bubbleTheirs,
          item.failed && styles.bubbleFailed,
        ]}
      >
        {item.text.length > 0 ? (
          <Text style={[styles.text, item.failed && styles.textFailed]}>{item.text}</Text>
        ) : (
          <Typing />
        )}
      </View>
    </View>
  );
}

/** Three dots standing in for a reply that has been accepted but has produced no tokens yet. */
function Typing() {
  return (
    <View style={styles.typing}>
      <View style={styles.dot} />
      <View style={styles.dot} />
      <View style={styles.dot} />
    </View>
  );
}

const styles = StyleSheet.create({
  row: {
    paddingHorizontal: theme.spacing,
    marginVertical: 4,
    flexDirection: 'row',
  },
  rowMine: { justifyContent: 'flex-end' },
  rowTheirs: { justifyContent: 'flex-start' },
  bubble: {
    maxWidth: '82%',
    paddingHorizontal: 14,
    paddingVertical: 10,
    borderRadius: theme.radius,
  },
  bubbleMine: {
    backgroundColor: theme.accentDim,
    borderBottomRightRadius: 4,
  },
  bubbleTheirs: {
    backgroundColor: theme.surfaceRaised,
    borderBottomLeftRadius: 4,
  },
  bubbleFailed: {
    backgroundColor: 'transparent',
    borderWidth: 1,
    borderColor: theme.danger,
  },
  text: {
    color: theme.text,
    fontSize: 16,
    lineHeight: 22,
  },
  textFailed: { color: theme.danger },
  noteRow: {
    paddingHorizontal: theme.spacing * 2,
    marginVertical: 10,
    alignItems: 'center',
  },
  note: {
    color: theme.textFaint,
    fontSize: 13,
    fontStyle: 'italic',
    textAlign: 'center',
  },
  typing: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingVertical: 4,
  },
  dot: {
    width: 6,
    height: 6,
    borderRadius: 3,
    marginHorizontal: 2,
    backgroundColor: theme.textDim,
  },
});
