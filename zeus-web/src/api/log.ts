// Zeus Log API client

export type LogEntry = {
  id: string;
  qsoDateTimeUtc: string;
  callsign: string;
  name: string | null;
  frequencyMhz: number;
  band: string;
  mode: string;
  rstSent: string;
  rstRcvd: string;
  grid: string | null;
  country: string | null;
  dxcc: number | null;
  cqZone: number | null;
  ituZone: number | null;
  state: string | null;
  comment: string | null;
  createdUtc: string;
  qrzLogId: string | null;
  qrzUploadedUtc: string | null;
};

export type CreateLogEntryRequest = {
  callsign: string;
  name?: string | null;
  frequencyMhz: number;
  band: string;
  mode: string;
  rstSent: string;
  rstRcvd: string;
  grid?: string | null;
  country?: string | null;
  dxcc?: number | null;
  cqZone?: number | null;
  ituZone?: number | null;
  state?: string | null;
  comment?: string | null;
  qsoDateTimeUtc?: string | null;
};

export type LogEntriesResponse = {
  entries: LogEntry[];
  totalCount: number;
};

export type QrzPublishRequest = {
  logEntryIds: string[];
};

export type QrzPublishResponse = {
  totalCount: number;
  successCount: number;
  failedCount: number;
  results: QrzPublishResult[];
};

export type QrzPublishResult = {
  logEntryId: string;
  success: boolean;
  qrzLogId: string | null;
  message: string | null;
};

// API functions

export async function getLogEntries(
  skip = 0,
  take = 100,
  signal?: AbortSignal
): Promise<LogEntriesResponse> {
  const url = `/api/log/entries?skip=${skip}&take=${take}`;
  const response = await fetch(url, { signal });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return await response.json();
}

export async function createLogEntry(
  request: CreateLogEntryRequest,
  signal?: AbortSignal
): Promise<LogEntry> {
  const response = await fetch('/api/log/entry', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
    signal,
  });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return await response.json();
}

export async function exportToAdif(signal?: AbortSignal): Promise<void> {
  const response = await fetch('/api/log/export/adif', { signal });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);

  const blob = await response.blob();
  const url = window.URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = `zeus-log-${new Date().toISOString().slice(0, 10)}.adi`;
  document.body.appendChild(a);
  a.click();
  window.URL.revokeObjectURL(url);
  document.body.removeChild(a);
}

export async function publishToQrz(
  request: QrzPublishRequest,
  signal?: AbortSignal
): Promise<QrzPublishResponse> {
  const response = await fetch('/api/log/publish/qrz', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
    signal,
  });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return await response.json();
}
