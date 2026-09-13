import React, { useEffect, useRef, useState } from 'react';
import { Pressable, ScrollView, StyleSheet, Switch, Text, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { LaneCredentials } from '../api/client';
import { VoiceSession, VoiceState, VoiceStats } from '../audio/session';
import { theme } from '../theme';

interface Props {
  credentials: LaneCredentials;
}

/**
 * A continuous audio session.
 *
 * It runs until End is pressed or this tab goes away — leaving the tab unmounts the screen,
 * and the cleanup stops the microphone rather than leaving it live behind another view.
 */
export function VoiceScreen({ credentials }: Props) {
  const [state, setState] = useState<VoiceState>('idle');
  const [diarize, setDiarize] = useState(false);
  const [level, setLevel] = useState(0);
  const [said, setSaid] = useState<string[]>([]);
  const [problem, setProblem] = useState<string | null>(null);
  const [stats, setStats] = useState<VoiceStats | null>(null);

  const session = useRef<VoiceSession | null>(null);

  useEffect(
    () => () => {
      void session.current?.stop();
      session.current = null;
    },
    [],
  );

  const start = async () => {
    setProblem(null);
    setSaid([]);
    setStats(null);

    const next = new VoiceSession(credentials, diarize, {
      onState: setState,
      onTranscript: text => setSaid(prev => [...prev, text]),
      onLevel: setLevel,
      onError: setProblem,
      onStats: setStats,
    });

    session.current = next;

    try {
      await next.start();
    } catch (error) {
      setProblem(error instanceof Error ? error.message : 'Could not start the microphone.');
      await next.stop();
    }
  };

  const stop = async () => {
    await session.current?.stop();
    session.current = null;
    setLevel(0);
  };

  const running = state !== 'idle';

  return (
    <SafeAreaView style={styles.root} edges={['top']}>
      <View style={styles.header}>
        <Text style={styles.title}>Voice</Text>
        <Text style={styles.subtitle}>{credentials.sessionKey}</Text>
      </View>

      <View style={styles.stage}>
        <Halo level={level} state={state} />
        <Text style={styles.state}>{describe(state)}</Text>
      </View>

      <ScrollView style={styles.transcript} contentContainerStyle={styles.transcriptContent}>
        {said.length === 0 ? (
          <Text style={styles.placeholder}>
            {running ? 'Listening. Say something.' : 'Start a session and talk to her.'}
          </Text>
        ) : (
          said.map((line, index) => (
            <Text key={index} style={styles.line}>
              {line}
            </Text>
          ))
        )}
      </ScrollView>

      {problem && <Text style={styles.problem}>{problem}</Text>}

      {stats && (
        <View style={styles.statsBox}>
          <Text style={styles.stats}>
            {`socket ${stats.socket} · ready ${stats.ready ? 'yes' : 'no'} · mic ${stats.inputRate} Hz · sent ${stats.sent}`}
          </Text>
          <Text style={styles.stats}>
            {`in: text ${stats.textFrames} · audio ${stats.audioFrames} (${Math.round(stats.receivedBytes / 1024)} KB) · other ${stats.otherFrames}`}
          </Text>
          <Text style={styles.stats}>
            {`speaker ${stats.outputRate} Hz ${stats.contextState} · enqueued ${stats.enqueued}`}
          </Text>
          {stats.lastText.length > 0 && (
            <Text style={styles.stats} numberOfLines={2}>{`last text: ${stats.lastText}`}</Text>
          )}
          {stats.lastError.length > 0 && (
            <Text style={[styles.stats, styles.statsError]} numberOfLines={3}>{`error: ${stats.lastError}`}</Text>
          )}
        </View>
      )}

      <View style={styles.controls}>
        <View style={styles.toggleRow}>
          <View style={styles.toggleText}>
            <Text style={styles.toggleLabel}>Tell speakers apart</Text>
            <Text style={styles.toggleHint}>
              For a microphone with several people on it. Off, the turn is attributed to you.
            </Text>
          </View>

          <Switch
            value={diarize}
            onValueChange={setDiarize}
            disabled={running}
            trackColor={{ false: theme.border, true: theme.accentDim }}
            thumbColor={diarize ? theme.accent : theme.textFaint}
          />
        </View>

        <Pressable
          style={({ pressed }) => [
            styles.button,
            running ? styles.buttonStop : styles.buttonStart,
            pressed && styles.pressed,
          ]}
          onPress={running ? stop : start}
          disabled={state === 'starting' || state === 'stopping'}
        >
          <Text style={[styles.buttonText, running && styles.buttonTextStop]}>
            {running ? 'End session' : 'Start session'}
          </Text>
        </Pressable>
      </View>
    </SafeAreaView>
  );
}

/** A ring that grows with whatever the microphone is hearing. */
function Halo({ level, state }: { level: number; state: VoiceState }) {
  const speaking = state === 'speaking';
  const size = 132 + Math.min(1, level * 6) * 48;

  return (
    <View style={styles.haloBox}>
      <View
        style={[
          styles.halo,
          {
            width: size,
            height: size,
            borderRadius: size / 2,
            borderColor: speaking ? theme.accent : theme.accentDim,
            opacity: state === 'idle' ? 0.25 : 1,
          },
        ]}
      />
    </View>
  );
}

function describe(state: VoiceState): string {
  switch (state) {
    case 'starting':
      return 'Opening the microphone…';
    case 'listening':
      return 'Listening';
    case 'speaking':
      return 'Lane is talking';
    case 'stopping':
      return 'Closing…';
    default:
      return 'Not running';
  }
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: theme.background },
  header: {
    paddingHorizontal: theme.spacing,
    paddingVertical: 12,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: theme.border,
  },
  title: { color: theme.text, fontSize: 20, fontWeight: '700' },
  subtitle: { color: theme.textFaint, fontSize: 12, marginTop: 2 },
  stage: { alignItems: 'center', paddingTop: 36, paddingBottom: 20 },
  haloBox: { height: 190, alignItems: 'center', justifyContent: 'center' },
  halo: { borderWidth: 2 },
  state: { color: theme.textDim, fontSize: 15, marginTop: 4 },
  transcript: { flex: 1 },
  transcriptContent: { paddingHorizontal: theme.spacing * 1.5, paddingBottom: 12 },
  placeholder: { color: theme.textFaint, fontSize: 15, textAlign: 'center', marginTop: 8 },
  line: { color: theme.text, fontSize: 16, lineHeight: 23, marginBottom: 12 },
  problem: {
    color: theme.danger,
    fontSize: 13.5,
    lineHeight: 19,
    paddingHorizontal: theme.spacing * 1.5,
    paddingBottom: 10,
  },
  statsBox: {
    paddingHorizontal: theme.spacing * 1.5,
    paddingBottom: 8,
    gap: 2,
  },
  stats: {
    color: theme.textFaint,
    fontSize: 11,
    fontVariant: ['tabular-nums'],
  },
  statsError: { color: theme.danger },
  controls: {
    paddingHorizontal: theme.spacing,
    paddingTop: 12,
    paddingBottom: 8,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: theme.border,
    backgroundColor: theme.surface,
  },
  toggleRow: { flexDirection: 'row', alignItems: 'center', marginBottom: 14, gap: 12 },
  toggleText: { flex: 1 },
  toggleLabel: { color: theme.text, fontSize: 15, fontWeight: '600' },
  toggleHint: { color: theme.textFaint, fontSize: 12.5, marginTop: 3, lineHeight: 17 },
  button: { height: 52, borderRadius: 12, alignItems: 'center', justifyContent: 'center' },
  buttonStart: { backgroundColor: theme.accent },
  buttonStop: { backgroundColor: 'transparent', borderWidth: 1, borderColor: theme.danger },
  pressed: { opacity: 0.75 },
  buttonText: { color: theme.background, fontSize: 16, fontWeight: '700' },
  buttonTextStop: { color: theme.danger },
});
