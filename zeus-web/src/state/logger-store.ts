import { create } from 'zustand';
import type { LogEntry, CreateLogEntryRequest, QrzPublishResponse } from '../api/log';
import { getLogEntries, createLogEntry, exportToAdif, publishToQrz } from '../api/log';

type LoggerState = {
  entries: LogEntry[];
  totalCount: number;
  loading: boolean;
  error: string | null;
  publishInFlight: boolean;
  publishError: string | null;
  lastPublishResult: QrzPublishResponse | null;
  selectedIds: Set<string>;

  // Actions
  loadEntries: () => Promise<void>;
  addLogEntry: (request: CreateLogEntryRequest) => Promise<LogEntry | null>;
  exportAdif: () => Promise<void>;
  publishSelectedToQrz: (logEntryIds: string[]) => Promise<void>;
  clearPublishResult: () => void;
  toggleSelected: (id: string) => void;
  clearSelected: () => void;
};

export const useLoggerStore = create<LoggerState>((set, get) => ({
  entries: [],
  totalCount: 0,
  loading: false,
  error: null,
  publishInFlight: false,
  publishError: null,
  lastPublishResult: null,
  selectedIds: new Set<string>(),

  loadEntries: async () => {
    set({ loading: true, error: null });
    try {
      const response = await getLogEntries(0, 100);
      set({ entries: response.entries, totalCount: response.totalCount, loading: false });
    } catch (err) {
      set({ error: err instanceof Error ? err.message : 'Failed to load log entries', loading: false });
    }
  },

  addLogEntry: async (request: CreateLogEntryRequest) => {
    set({ error: null });
    try {
      const entry = await createLogEntry(request);
      // Reload entries to get the updated list
      await get().loadEntries();
      return entry;
    } catch (err) {
      set({ error: err instanceof Error ? err.message : 'Failed to create log entry' });
      return null;
    }
  },

  exportAdif: async () => {
    set({ error: null });
    try {
      await exportToAdif();
    } catch (err) {
      set({ error: err instanceof Error ? err.message : 'Failed to export ADIF' });
    }
  },

  publishSelectedToQrz: async (logEntryIds: string[]) => {
    set({ publishInFlight: true, publishError: null, lastPublishResult: null });
    try {
      const result = await publishToQrz({ logEntryIds });
      set({ lastPublishResult: result, publishInFlight: false, selectedIds: new Set<string>() });
      // Reload entries to update QRZ sync status
      await get().loadEntries();
    } catch (err) {
      set({
        publishError: err instanceof Error ? err.message : 'Failed to publish to QRZ',
        publishInFlight: false,
      });
    }
  },

  clearPublishResult: () => {
    set({ lastPublishResult: null, publishError: null });
  },

  toggleSelected: (id: string) => {
    const next = new Set(get().selectedIds);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    set({ selectedIds: next });
  },

  clearSelected: () => set({ selectedIds: new Set<string>() }),
}));

// Load entries on module load
useLoggerStore.getState().loadEntries();
