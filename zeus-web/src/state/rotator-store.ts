import { create } from 'zustand';
import {
  getRotatorStatus,
  postRotatorConfig,
  setRotatorAz,
  stopRotator,
  testRotator,
  type RotctldConfig,
  type RotctldStatus,
  type RotctldTestResult,
} from '../api/rotator';

// Persist the host/port/enabled/interval the user has chosen so the pill
// stays usable across reloads. The backend is the source of truth while
// connected; this is just the form-default memory.
const CONFIG_STORAGE_KEY = 'zeus.rotator.config';
const DEFAULT_CONFIG: RotctldConfig = {
  enabled: false,
  host: '127.0.0.1',
  port: 4533,
  pollingIntervalMs: 500,
};

function readSavedConfig(): RotctldConfig {
  try {
    if (typeof localStorage === 'undefined') return DEFAULT_CONFIG;
    const raw = localStorage.getItem(CONFIG_STORAGE_KEY);
    if (!raw) return DEFAULT_CONFIG;
    const parsed = JSON.parse(raw) as Partial<RotctldConfig>;
    return {
      enabled: Boolean(parsed.enabled),
      host: typeof parsed.host === 'string' && parsed.host ? parsed.host : DEFAULT_CONFIG.host,
      port: typeof parsed.port === 'number' && parsed.port > 0 ? parsed.port : DEFAULT_CONFIG.port,
      pollingIntervalMs:
        typeof parsed.pollingIntervalMs === 'number' && parsed.pollingIntervalMs > 0
          ? parsed.pollingIntervalMs
          : DEFAULT_CONFIG.pollingIntervalMs,
    };
  } catch {
    return DEFAULT_CONFIG;
  }
}

function writeSavedConfig(cfg: RotctldConfig): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(CONFIG_STORAGE_KEY, JSON.stringify(cfg));
  } catch {
    /* quota — silent */
  }
}

export type RotatorStoreState = {
  config: RotctldConfig;
  status: RotctldStatus | null;
  testInFlight: boolean;
  lastTestResult: RotctldTestResult | null;

  refreshStatus: () => Promise<void>;
  saveConfig: (cfg: RotctldConfig) => Promise<RotctldStatus>;
  setAzimuth: (az: number) => Promise<RotctldStatus | null>;
  stop: () => Promise<void>;
  test: (host: string, port: number) => Promise<RotctldTestResult>;
};

export const useRotatorStore = create<RotatorStoreState>((set) => ({
  config: readSavedConfig(),
  status: null,
  testInFlight: false,
  lastTestResult: null,

  refreshStatus: async () => {
    try {
      const status = await getRotatorStatus();
      set({ status });
    } catch {
      /* transient — next poll recovers */
    }
  },

  saveConfig: async (cfg) => {
    writeSavedConfig(cfg);
    const status = await postRotatorConfig(cfg);
    set({ config: cfg, status });
    return status;
  },

  setAzimuth: async (az) => {
    try {
      const status = await setRotatorAz(az);
      set({ status });
      return status;
    } catch {
      return null;
    }
  },

  stop: async () => {
    try {
      const status = await stopRotator();
      set({ status });
    } catch {
      /* ignore */
    }
  },

  test: async (host, port) => {
    set({ testInFlight: true, lastTestResult: null });
    const result = await testRotator(host, port);
    set({ testInFlight: false, lastTestResult: result });
    return result;
  },
}));

// Kick off an initial status probe at module load, then poll at 1 s while the
// page is alive. When rotctld is enabled and connected, this keeps the pill's
// current-az readout in sync without the UI having to subscribe to a WS stream.
if (typeof window !== 'undefined') {
  void useRotatorStore.getState().refreshStatus();
  window.setInterval(() => {
    void useRotatorStore.getState().refreshStatus();
  }, 1000);

  // Push the saved config to the backend on first load so the service is in the
  // same state the UI will render (Enabled flag, host/port). Backend state is
  // in-memory only — a restart resets to defaults.
  const initial = useRotatorStore.getState().config;
  if (initial.enabled) {
    void useRotatorStore.getState().saveConfig(initial);
  }
}
