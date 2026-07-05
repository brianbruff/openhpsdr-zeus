// SPDX-License-Identifier: GPL-2.0-or-later
//
// PluginBrowser — registry catalog browser. Lists entries from
// /api/plugins/registry; each card shows the latest version, description,
// categories and verified-by-Zeus badge. The Install button posts
// { source: "registry", id, version } to the install endpoint.

import { useEffect, useMemo, useState } from 'react';

import { usePluginsStore } from '../state/plugins-store';
import type { RegistryPluginEntry } from '../api/plugins';
import { createPluginCheckout, registryEntryToManagedPlugin } from '../api/plugins';
import { latestRegistryVersion } from '../updates';
import { RestartRequiredModal } from '../../components/RestartRequiredModal';
import { pluginAccessFor, useUserAccessStore } from '../../state/user-access-store';

function VerifiedBadge() {
  return (
    <span
      title="Verified by the Zeus maintainers"
      style={{
        fontSize: 10,
        padding: '2px 6px',
        borderRadius: 'var(--r-sm)',
        background: 'var(--ok-soft)',
        border: '1px solid var(--ok)',
        color: 'var(--ok)',
        fontWeight: 700,
        letterSpacing: '0.08em',
        textTransform: 'uppercase',
      }}
    >
      VERIFIED
    </span>
  );
}

function CategoryChip({ label }: { label: string }) {
  return (
    <span
      style={{
        fontSize: 10,
        padding: '2px 6px',
        borderRadius: 'var(--r-sm)',
        background: 'var(--bg-2)',
        border: '1px solid var(--line)',
        color: 'var(--fg-2)',
        fontFamily: 'var(--font-mono)',
      }}
    >
      {label}
    </span>
  );
}

function subscriptionPrice(pluginAccess: ReturnType<typeof pluginAccessFor>): string | null {
  const managed = pluginAccess.managedPlugin;
  if (!managed?.subscriptionRequired) return null;
  if (managed.monthlyPriceCents <= 0) return `${managed.currency} manual subscription`;
  return `${managed.currency} ${(managed.monthlyPriceCents / 100).toFixed(2)}/mo`;
}

function RegistryCard({
  entry,
  installedIds,
}: {
  entry: RegistryPluginEntry;
  installedIds: Set<string>;
}) {
  const install = usePluginsStore((s) => s.install);
  const installing = usePluginsStore((s) => s.installInflight);
  const refreshUserSession = useUserAccessStore((s) => s.refreshSession);
  const latest = useMemo(() => latestRegistryVersion(entry), [entry]);
  const alreadyInstalled = installedIds.has(entry.id);
  const registryManagedPlugin = useMemo(() => registryEntryToManagedPlugin(entry), [entry]);
  const pluginAccess = pluginAccessFor(entry.id, false, registryManagedPlugin);
  const price = subscriptionPrice(pluginAccess);

  // Restart-required modal — fires after a successful install of THIS
  // card's plugin. The plugin host registers new endpoints +
  // AssemblyLoadContexts only at backend startup, so the operator has
  // to restart Zeus to bring the new install into the live process.
  // Same modal the Download Audio Suite bundle install uses.
  const [restartModalOpen, setRestartModalOpen] = useState(false);
  const [installedDisplayName, setInstalledDisplayName] = useState<string | null>(null);
  const [checkoutInflight, setCheckoutInflight] = useState(false);
  const [checkoutError, setCheckoutError] = useState<string | null>(null);

  const onInstall = async () => {
    if (!pluginAccess.allowed) {
      if (checkoutInflight) return;
      setCheckoutInflight(true);
      setCheckoutError(null);
      try {
        const checkout = await createPluginCheckout([entry.id]);
        if (checkout.url) {
          window.location.href = checkout.url;
          return;
        }
        if (checkout.subscriptionUpdated) {
          await refreshUserSession();
          setCheckoutError(null);
          return;
        }
        setCheckoutError('Subscription checkout did not return a billing session.');
      } catch (err) {
        setCheckoutError(err instanceof Error ? err.message : String(err));
      } finally {
        setCheckoutInflight(false);
      }
      return;
    }
    if (!latest || installing) return;
    const dto = await install({
      source: 'registry',
      id: entry.id,
      version: latest.version,
    });
    if (dto) {
      setInstalledDisplayName(`${dto.name} v${dto.version}`);
      setRestartModalOpen(true);
    }
  };

  return (
    <div
      style={{
        background: 'var(--bg-2)',
        border: '1px solid var(--panel-border)',
        borderRadius: 'var(--r-md)',
        padding: 12,
        display: 'flex',
        flexDirection: 'column',
        gap: 8,
      }}
    >
      <div
        style={{
          display: 'flex',
          alignItems: 'baseline',
          justifyContent: 'space-between',
          gap: 12,
        }}
      >
        <div>
          <div
            style={{
              fontWeight: 700,
              color: 'var(--fg-0)',
              fontSize: 13,
              display: 'flex',
              alignItems: 'center',
              gap: 8,
            }}
          >
            {entry.name}
            {entry.verified && <VerifiedBadge />}
          </div>
          <div style={{ color: 'var(--fg-2)', fontSize: 11 }}>
            {latest ? `v${latest.version}` : 'No versions'}
          </div>
        </div>
        <button
          type="button"
          className="btn sm"
          onClick={onInstall}
          disabled={!latest || installing || checkoutInflight || alreadyInstalled}
          title={!pluginAccess.allowed ? pluginAccess.reason ?? 'Plugin subscription required' : undefined}
          aria-label={
            !pluginAccess.allowed
              ? `${entry.name} requires a plugin subscription`
              : alreadyInstalled
                ? `${entry.name} already installed`
                : `Install ${entry.name}`
          }
        >
          {!pluginAccess.allowed
            ? checkoutInflight ? 'WORKING…' : 'SUBSCRIBE'
            : alreadyInstalled
              ? 'INSTALLED'
              : installing || checkoutInflight
                ? 'WORKING…'
                : 'INSTALL'}
        </button>
      </div>

      {!pluginAccess.allowed && (
        <div style={{ color: 'var(--accent)', fontSize: 11, fontFamily: 'var(--font-mono)' }}>
          {price
            ? `${pluginAccess.reason ?? 'Plugin subscription required'} - ${price}`
            : pluginAccess.reason ?? 'Plugin subscription required'}
        </div>
      )}

      {checkoutError && (
        <div style={{ color: 'var(--tx)', fontSize: 11 }}>
          {checkoutError}
        </div>
      )}

      {entry.description && (
        <div style={{ color: 'var(--fg-1)', lineHeight: 1.5 }}>
          {entry.description}
        </div>
      )}

      {entry.categories.length > 0 && (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
          {entry.categories.map((c) => (
            <CategoryChip key={c} label={c} />
          ))}
        </div>
      )}

      {restartModalOpen && (
        <RestartRequiredModal
          pluginDisplayName={installedDisplayName ?? entry.name}
          onClose={() => setRestartModalOpen(false)}
        />
      )}
    </div>
  );
}

export function PluginBrowser() {
  const registry = usePluginsStore((s) => s.registry);
  const load = usePluginsStore((s) => s.registryLoad);
  const refresh = usePluginsStore((s) => s.refreshRegistry);
  const installed = usePluginsStore((s) => s.installed);
  const installError = usePluginsStore((s) => s.lastInstallError);
  const installOk = usePluginsStore((s) => s.lastInstallOk);
  const clearInstall = usePluginsStore((s) => s.clearInstallFeedback);

  useEffect(() => {
    if (!load.loaded && !load.inflight) {
      void refresh();
    }
  }, [load.loaded, load.inflight, refresh]);

  const installedIds = useMemo(
    () => new Set(installed.map((p) => p.id)),
    [installed],
  );

  return (
    <div
      data-testid="plugins-browser"
      style={{ display: 'flex', flexDirection: 'column', gap: 12 }}
    >
      <div
        style={{
          display: 'flex',
          alignItems: 'baseline',
          justifyContent: 'flex-end',
          gap: 12,
        }}
      >
        <button
          type="button"
          className="btn sm"
          onClick={() => void refresh()}
          disabled={load.inflight}
        >
          {load.inflight ? 'LOADING…' : 'RELOAD'}
        </button>
      </div>

      {installOk && (
        <div
          role="status"
          style={{
            padding: 10,
            borderRadius: 'var(--r-sm)',
            background: 'var(--ok-soft)',
            border: '1px solid var(--ok)',
            color: 'var(--fg-0)',
            display: 'flex',
            justifyContent: 'space-between',
            gap: 8,
          }}
        >
          <span>{installOk}</span>
          <button
            type="button"
            className="btn sm"
            onClick={clearInstall}
            aria-label="Dismiss notice"
          >
            ×
          </button>
        </div>
      )}

      {installError && (
        <div
          role="alert"
          style={{
            padding: 10,
            borderRadius: 'var(--r-sm)',
            background: 'var(--tx-soft)',
            border: '1px solid var(--tx)',
            color: 'var(--fg-0)',
            display: 'flex',
            justifyContent: 'space-between',
            gap: 8,
          }}
        >
          <span>Install failed: {installError}</span>
          <button
            type="button"
            className="btn sm"
            onClick={clearInstall}
            aria-label="Dismiss install error"
          >
            ×
          </button>
        </div>
      )}

      {load.loadError && (
        <div
          role="alert"
          style={{
            padding: 10,
            borderRadius: 'var(--r-sm)',
            background: 'var(--tx-soft)',
            border: '1px solid var(--tx)',
            color: 'var(--fg-0)',
          }}
        >
          Couldn’t reach the registry: {load.loadError}
        </div>
      )}

      {!load.loaded && load.inflight && (
        <div style={{ color: 'var(--fg-2)' }}>Loading registry catalog…</div>
      )}

      {load.loaded && registry && registry.plugins.length === 0 && (
        <div
          style={{
            padding: 16,
            background: 'var(--bg-2)',
            border: '1px dashed var(--line-strong)',
            borderRadius: 'var(--r-md)',
            color: 'var(--fg-2)',
            textAlign: 'center',
          }}
        >
          Registry is empty — no plugins to show.
        </div>
      )}

      {registry?.plugins.map((entry) => (
        <RegistryCard
          key={entry.id}
          entry={entry}
          installedIds={installedIds}
        />
      ))}
    </div>
  );
}
