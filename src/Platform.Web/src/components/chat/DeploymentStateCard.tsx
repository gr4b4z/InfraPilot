import { useNavigate } from 'react-router-dom';
import { ExternalLink } from 'lucide-react';
import { formatDistanceToNow } from 'date-fns';

interface CellData {
  service: string;
  environment: string;
  version: string;
  previousVersion?: string;
  deployedAt: string;
}

interface StateData {
  product?: string | null;
  services: string[];
  environments: string[];
  cells: CellData[];
}

interface Props {
  title?: string;
  data: StateData | unknown;
}

export function DeploymentStateCard({ title, data }: Props) {
  const navigate = useNavigate();
  const d = data as StateData;

  if (!d?.services?.length) {
    return (
      <div
        className="mt-2 p-3 rounded-lg text-xs"
        style={{ backgroundColor: 'var(--bg-tertiary)', color: 'var(--text-muted)' }}
      >
        No deployment data found.
      </div>
    );
  }

  const getCell = (service: string, env: string) =>
    d.cells.find((c) => c.service === service && c.environment === env);

  return (
    <div
      className="mt-2 rounded-lg overflow-hidden border"
      style={{ borderColor: 'var(--border-color)' }}
    >
      {title && (
        <div
          className="px-3 py-2 text-xs font-semibold flex items-center justify-between"
          style={{ backgroundColor: 'var(--bg-tertiary)', color: 'var(--text-secondary)' }}
        >
          <span>{title}</span>
          {d.product && (
            <button
              onClick={() => navigate(`/deployments/${d.product}`)}
              className="flex items-center gap-1 hover:opacity-80 transition-opacity"
              style={{ color: 'var(--accent)' }}
            >
              Open <ExternalLink size={11} />
            </button>
          )}
        </div>
      )}
      <div className="overflow-x-auto">
        <table className="w-full text-[11px]">
          <thead>
            <tr style={{ borderBottom: '1px solid var(--border-color)' }}>
              <th
                className="text-left px-2 py-1.5 font-medium"
                style={{ color: 'var(--text-muted)' }}
              >
                Service
              </th>
              {d.environments.map((env) => (
                <th
                  key={env}
                  className="text-center px-2 py-1.5 font-medium"
                  style={{ color: 'var(--text-muted)' }}
                >
                  {env}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {d.services.map((service) => (
              <tr
                key={service}
                style={{ borderBottom: '1px solid var(--border-color)' }}
              >
                <td
                  className="px-2 py-1.5 font-medium whitespace-nowrap"
                  style={{ color: 'var(--text-primary)' }}
                >
                  {service}
                </td>
                {d.environments.map((env) => {
                  const cell = getCell(service, env);
                  if (!cell) {
                    return (
                      <td
                        key={env}
                        className="text-center px-2 py-1.5"
                        style={{ color: 'var(--text-muted)' }}
                      >
                        —
                      </td>
                    );
                  }
                  return (
                    <td
                      key={env}
                      className="text-center px-2 py-1.5"
                    >
                      <div
                        className="font-mono text-[10px] font-medium"
                        style={{ color: 'var(--text-primary)' }}
                      >
                        v{cell.version}
                      </div>
                      <div
                        className="text-[9px]"
                        style={{ color: 'var(--text-muted)' }}
                      >
                        {formatDistanceToNow(new Date(cell.deployedAt), { addSuffix: true })}
                      </div>
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
