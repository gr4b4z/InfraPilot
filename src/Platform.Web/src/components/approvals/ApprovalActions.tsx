import { useState } from 'react';
import { Check, X, MessageSquare, Loader2 } from 'lucide-react';

interface Props {
  onApprove: (comment: string) => void;
  onReject: (comment: string) => void;
  onRequestChanges: (comment: string) => void;
  loading?: boolean;
}

export function ApprovalActions({ onApprove, onReject, onRequestChanges, loading }: Props) {
  const [comment, setComment] = useState('');
  const [pendingAction, setPendingAction] = useState<'reject' | 'changes' | null>(null);

  function handleApprove() {
    onApprove(comment);
  }

  function handleReject() {
    if (!comment.trim()) {
      setPendingAction('reject');
      return;
    }
    onReject(comment);
  }

  function handleRequestChanges() {
    if (!comment.trim()) {
      setPendingAction('changes');
      return;
    }
    onRequestChanges(comment);
  }

  const needsComment = pendingAction !== null && !comment.trim();

  return (
    <div className="space-y-4">
      <div>
        <label className="flex items-center gap-1.5 text-[12px] font-medium mb-2" style={{ color: 'var(--text-secondary)' }}>
          <MessageSquare size={13} />
          Comment
          {pendingAction && (
            <span className="text-[11px] ml-1" style={{ color: 'var(--danger)' }}>
              (required for {pendingAction === 'reject' ? 'rejection' : 'requesting changes'})
            </span>
          )}
        </label>
        <textarea
          value={comment}
          onChange={(e) => { setComment(e.target.value); setPendingAction(null); }}
          placeholder="Add your review comment..."
          rows={3}
          className="w-full px-3 py-2 text-[13px] rounded-lg border outline-none transition-colors resize-y"
          style={{
            backgroundColor: 'var(--bg-secondary)',
            borderColor: needsComment ? 'var(--danger)' : 'var(--border-color)',
            color: 'var(--text-primary)',
          }}
        />
      </div>

      <div className="flex flex-wrap gap-2">
        <button
          type="button"
          onClick={handleApprove}
          disabled={loading}
          className="flex items-center gap-1.5 px-4 py-2 text-[13px] font-medium rounded-lg text-white transition-all hover:opacity-90 disabled:opacity-50"
          style={{ backgroundColor: 'var(--success)' }}
        >
          {loading ? <Loader2 size={14} className="animate-spin" /> : <Check size={14} />}
          Approve
        </button>
        <button
          type="button"
          onClick={handleReject}
          disabled={loading}
          className="flex items-center gap-1.5 px-4 py-2 text-[13px] font-medium rounded-lg text-white transition-all hover:opacity-90 disabled:opacity-50"
          style={{ backgroundColor: 'var(--danger)' }}
        >
          {loading ? <Loader2 size={14} className="animate-spin" /> : <X size={14} />}
          Reject
        </button>
        <button
          type="button"
          onClick={handleRequestChanges}
          disabled={loading}
          className="flex items-center gap-1.5 px-4 py-2 text-[13px] font-medium rounded-lg transition-all hover:opacity-90 disabled:opacity-50"
          style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--warning)', border: '1px solid var(--warning)' }}
        >
          {loading ? <Loader2 size={14} className="animate-spin" /> : <MessageSquare size={14} />}
          Request Changes
        </button>
      </div>
    </div>
  );
}
