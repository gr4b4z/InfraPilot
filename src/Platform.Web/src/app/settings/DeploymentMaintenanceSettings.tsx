import { useState } from 'react';
import { Trash2, Check } from 'lucide-react';
import { api } from '@/lib/api';

export function DeploymentMaintenanceSettings() {
  const [scanResult, setScanResult] = useState<{ groups: number; rows: number } | null>(null);
  const [scanning, setScanning] = useState(false);
  const [removing, setRemoving] = useState(false);
  const [removedResult, setRemovedResult] = useState<{ groups: number; rows: number } | null>(null);
  const [error, setError] = useState<string | null>(null);

  const handleScan = async () => {
    setScanning(true);
    setError(null);
    setRemovedResult(null);
    try {
      const result = await api.getDeploymentDuplicatesPreview();
      setScanResult(result);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to scan for duplicates');
    } finally {
      setScanning(false);
    }
  };

  const handleRemove = async () => {
    setRemoving(true);
    setError(null);
    try {
      const result = await api.removeDeploymentDuplicates();
      setRemovedResult(result);
      setScanResult(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to remove duplicates');
    } finally {
      setRemoving(false);
    }
  };

  const handleCancel = () => {
    setScanResult(null);
    setError(null);
  };

  return (
    <section
      className="rounded-xl border p-5 space-y-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div>
        <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
          Deployment Maintenance
        </h2>
        <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          Duplicate deployment events can accumulate when CI systems retry ingest webhooks. Scan to
          see how many exist, then remove them. Duplicates are rows matching on product, service,
          environment, version, deployedAt and source — the earliest ingested row per group is kept.
        </p>
      </div>

      <div className="flex items-center gap-3 flex-wrap">
        {!scanResult && (
          <button
            onClick={handleScan}
            disabled={scanning}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
            style={{ backgroundColor: 'var(--accent)' }}
          >
            {scanning ? 'Scanning…' : 'Scan for duplicates'}
          </button>
        )}

        {scanResult && scanResult.rows === 0 && (
          <>
            <span className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
              No duplicates found.
            </span>
            <button
              onClick={handleCancel}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              Dismiss
            </button>
          </>
        )}

        {scanResult && scanResult.rows > 0 && (
          <>
            <span className="text-[13px]" style={{ color: 'var(--text-primary)' }}>
              Found <strong>{scanResult.rows}</strong>{' '}
              {scanResult.rows === 1 ? 'duplicate' : 'duplicates'} across{' '}
              <strong>{scanResult.groups}</strong>{' '}
              {scanResult.groups === 1 ? 'group' : 'groups'}.
            </span>
            <button
              onClick={handleRemove}
              disabled={removing}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
              style={{ backgroundColor: 'var(--danger, #dc2626)' }}
            >
              <Trash2 size={14} />
              {removing ? 'Removing…' : 'Remove duplicates'}
            </button>
            <button
              onClick={handleCancel}
              disabled={removing}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              Cancel
            </button>
          </>
        )}

        {removedResult && (
          <span className="inline-flex items-center gap-1 text-[13px]" style={{ color: 'var(--success)' }}>
            <Check size={14} />
            Removed {removedResult.rows}{' '}
            {removedResult.rows === 1 ? 'duplicate' : 'duplicates'} across{' '}
            {removedResult.groups} {removedResult.groups === 1 ? 'group' : 'groups'}.
          </span>
        )}
      </div>

      {error && (
        <div
          className="text-[13px] rounded-lg px-3 py-2"
          style={{ color: 'var(--danger, #dc2626)', backgroundColor: 'var(--danger-muted, #fee2e2)' }}
        >
          {error}
        </div>
      )}
    </section>
  );
}
