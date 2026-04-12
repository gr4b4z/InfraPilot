import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { StatusBadge } from '@/components/requests/StatusBadge';
import type { ServiceRequest } from '@/lib/types';
import { api } from '@/lib/api';
import { formatDistanceToNow } from 'date-fns';
import { FileText, ArrowUpRight, Inbox, User, Users } from 'lucide-react';

type Scope = 'mine' | 'all';

export function RequestsPage() {
  const [requests, setRequests] = useState<ServiceRequest[]>([]);
  const [loading, setLoading] = useState(true);
  const [scope, setScope] = useState<Scope>('mine');
  const navigate = useNavigate();

  useEffect(() => {
    setLoading(true);
    const params: Record<string, string> = {};
    if (scope === 'all') params.scope = 'all';
    api.getRequests(params)
      .then((data) => setRequests(data.items || []))
      .catch(() => setRequests([]))
      .finally(() => setLoading(false));
  }, [scope]);

  const statusCounts = {
    total: requests.length,
    active: requests.filter((r) => !['Completed', 'Rejected', 'Failed'].includes(r.status)).length,
    completed: requests.filter((r) => r.status === 'Completed').length,
  };

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
            Requests
          </h1>
          <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
            {scope === 'mine' ? 'Track your infrastructure service requests' : 'All team infrastructure requests'}
          </p>
        </div>
        <button
          onClick={() => navigate('/catalog')}
          className="flex items-center gap-2 px-4 py-2 text-[13px] font-medium rounded-lg text-white transition-colors"
          style={{ backgroundColor: 'var(--accent)' }}
        >
          <FileText size={14} />
          New Request
        </button>
      </div>

      {/* Scope toggle + stats */}
      <div className="flex items-center justify-between gap-4">
        {/* Segmented control */}
        <div
          className="inline-flex rounded-lg p-0.5 gap-0.5"
          style={{ backgroundColor: 'var(--bg-secondary)', border: '1px solid var(--border-color)' }}
        >
          <button
            onClick={() => setScope('mine')}
            className="flex items-center gap-1.5 px-3.5 py-1.5 text-[13px] font-medium rounded-md transition-all"
            style={{
              backgroundColor: scope === 'mine' ? 'var(--bg-primary)' : 'transparent',
              color: scope === 'mine' ? 'var(--text-primary)' : 'var(--text-muted)',
              boxShadow: scope === 'mine' ? '0 1px 2px rgba(0,0,0,0.06)' : 'none',
            }}
          >
            <User size={13} />
            My Requests
          </button>
          <button
            onClick={() => setScope('all')}
            className="flex items-center gap-1.5 px-3.5 py-1.5 text-[13px] font-medium rounded-md transition-all"
            style={{
              backgroundColor: scope === 'all' ? 'var(--bg-primary)' : 'transparent',
              color: scope === 'all' ? 'var(--text-primary)' : 'var(--text-muted)',
              boxShadow: scope === 'all' ? '0 1px 2px rgba(0,0,0,0.06)' : 'none',
            }}
          >
            <Users size={13} />
            All Requests
          </button>
        </div>

        {/* Quick stats */}
        <div className="flex items-center gap-3 text-[12px]" style={{ color: 'var(--text-muted)' }}>
          <span>{statusCounts.total} total</span>
          <span className="w-px h-3" style={{ backgroundColor: 'var(--border-color)' }} />
          <span style={{ color: 'var(--accent)' }}>{statusCounts.active} active</span>
          <span className="w-px h-3" style={{ backgroundColor: 'var(--border-color)' }} />
          <span style={{ color: 'var(--success)' }}>{statusCounts.completed} completed</span>
        </div>
      </div>

      {/* Table */}
      {loading ? (
        <div className="space-y-2">
          {[1, 2, 3, 4].map((i) => (
            <div key={i} className="skeleton h-14" />
          ))}
        </div>
      ) : requests.length === 0 ? (
        <div
          className="flex flex-col items-center justify-center py-20 rounded-xl border"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
        >
          <div
            className="w-12 h-12 rounded-xl flex items-center justify-center mb-4"
            style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}
          >
            <Inbox size={24} />
          </div>
          <p className="text-[14px] font-medium" style={{ color: 'var(--text-primary)' }}>
            {scope === 'mine' ? 'No requests yet' : 'No team requests found'}
          </p>
          <p className="text-[13px] mt-1 mb-4" style={{ color: 'var(--text-muted)' }}>
            {scope === 'mine'
              ? 'Browse the catalog to create your first request'
              : 'No infrastructure requests have been submitted yet'}
          </p>
          {scope === 'mine' && (
            <button
              onClick={() => navigate('/catalog')}
              className="text-[13px] font-medium px-4 py-2 rounded-lg transition-colors"
              style={{ backgroundColor: 'var(--accent-muted)', color: 'var(--accent)' }}
            >
              Browse Catalog
            </button>
          )}
        </div>
      ) : (
        <div
          className="rounded-xl border overflow-hidden"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
        >
          <table className="w-full text-[13px]">
            <thead>
              <tr style={{ backgroundColor: 'var(--bg-secondary)' }}>
                <th className="text-left px-4 py-3 font-medium text-[11px] uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>Service</th>
                <th className="text-left px-4 py-3 font-medium text-[11px] uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>Requester</th>
                <th className="text-left px-4 py-3 font-medium text-[11px] uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>Status</th>
                <th className="text-left px-4 py-3 font-medium text-[11px] uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>Submitted</th>
                <th className="text-left px-4 py-3 font-medium text-[11px] uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>Last Updated</th>
                <th className="w-10"></th>
              </tr>
            </thead>
            <tbody>
              {requests.map((req) => (
                <tr
                  key={req.id}
                  onClick={() => navigate(`/requests/${req.id}`)}
                  className="border-t cursor-pointer table-row-hover transition-colors"
                  style={{ borderColor: 'var(--border-color)' }}
                >
                  <td className="px-4 py-3.5 font-medium" style={{ color: 'var(--text-primary)' }}>
                    {req.catalogItem?.name || 'Unknown service'}
                  </td>
                  <td className="px-4 py-3.5" style={{ color: 'var(--text-secondary)' }}>
                    <div className="flex items-center gap-2">
                      {scope === 'all' && (
                        <div
                          className="w-5 h-5 rounded-full flex items-center justify-center text-[9px] font-bold shrink-0"
                          style={{ backgroundColor: 'var(--accent-muted)', color: 'var(--accent)' }}
                        >
                          {(req.requesterName || '?').charAt(0).toUpperCase()}
                        </div>
                      )}
                      <span>{req.requesterName}</span>
                    </div>
                  </td>
                  <td className="px-4 py-3.5">
                    <StatusBadge status={req.status} />
                  </td>
                  <td className="px-4 py-3.5" style={{ color: 'var(--text-muted)' }}>
                    {formatDistanceToNow(new Date(req.createdAt), { addSuffix: true })}
                  </td>
                  <td className="px-4 py-3.5" style={{ color: 'var(--text-muted)' }}>
                    {formatDistanceToNow(new Date(req.updatedAt), { addSuffix: true })}
                  </td>
                  <td className="px-2 py-3.5">
                    <ArrowUpRight size={14} style={{ color: 'var(--text-muted)' }} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {/* Pagination stub */}
          <div
            className="flex items-center justify-between px-4 py-3 border-t text-[12px]"
            style={{ borderColor: 'var(--border-color)', color: 'var(--text-muted)' }}
          >
            <span>Showing {requests.length} {scope === 'mine' ? 'of your' : 'team'} requests</span>
            <div className="flex gap-1">
              <button className="px-3 py-1 rounded-md border" style={{ borderColor: 'var(--border-color)' }} disabled>Previous</button>
              <button className="px-3 py-1 rounded-md border" style={{ borderColor: 'var(--border-color)' }} disabled>Next</button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
