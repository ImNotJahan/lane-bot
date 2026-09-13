import * as SecureStore from 'expo-secure-store';
import { LaneCredentials } from '../api/client';

const KEY = 'lane.credentials';

export const DEFAULT_SESSION_KEY = 'shared:pocket';

export async function loadCredentials(): Promise<LaneCredentials | null> {
  try {
    const raw = await SecureStore.getItemAsync(KEY);

    if (!raw) return null;

    const parsed = JSON.parse(raw) as Partial<LaneCredentials>;

    if (!parsed.baseUrl || !parsed.clientKey) return null;

    return {
      baseUrl: parsed.baseUrl,
      clientKey: parsed.clientKey,
      sessionKey: parsed.sessionKey || DEFAULT_SESSION_KEY,
    };
  } catch {
    // A keychain that cannot be read is indistinguishable from an empty one here.
    return null;
  }
}

export async function saveCredentials(credentials: LaneCredentials): Promise<void> {
  await SecureStore.setItemAsync(KEY, JSON.stringify(credentials));
}

export async function clearCredentials(): Promise<void> {
  await SecureStore.deleteItemAsync(KEY);
}
