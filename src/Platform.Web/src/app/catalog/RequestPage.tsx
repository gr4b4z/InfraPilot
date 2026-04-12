import { useParams, useNavigate, Link } from 'react-router-dom';
import { useEffect, useState } from 'react';
import { A2UIRenderer } from '@/components/a2ui/A2UIRenderer';
import { Loader2, CheckCircle, ArrowLeft, FileText, Shield, Send } from 'lucide-react';
import { useConversationStore } from '@/stores/conversationStore';
import { api } from '@/lib/api';
import { buildAgentUrl } from '@/lib/runtimeConfig';
import type { A2UIComponent } from '@/lib/types';

interface CatalogDetail {
  item: { id: string; slug: string; name: string; description: string; category: string; icon: string };
  inputs: Array<{
    id: string;
    component: string;
    label: string;
    placeholder?: string;
    validation?: string;
    required: boolean;
    default?: unknown;
    source?: string;
    options?: Array<{ id: string; label: string }>;
    visibleWhen?: { field: string; equals: unknown };
    min?: number;
    max?: number;
    step?: number;
  }>;
}

function mapInputsToA2UI(inputs: CatalogDetail['inputs']): A2UIComponent[] {
  const componentTypeMap: Record<string, string> = {
    TextInput: 'text-input', Select: 'select', MultiSelect: 'multi-select',
    Toggle: 'toggle', NumberInput: 'number-input', SecretField: 'secret-field',
    CodeBlock: 'code-block', KeyValueList: 'key-value-list',
    ResourcePicker: 'resource-picker', UserPicker: 'user-picker',
    EnvironmentSelector: 'environment-selector', FileUpload: 'file-upload',
    TextArea: 'text-area',
  };

  const components: A2UIComponent[] = inputs.map((input) => ({
    type: componentTypeMap[input.component] || input.component.toLowerCase(),
    id: input.id, label: input.label, placeholder: input.placeholder,
    required: input.required, dataKey: input.id, options: input.options,
    defaultValue: input.default, source: input.source,
    visibleWhen: input.visibleWhen ? { field: input.visibleWhen.field, equals: input.visibleWhen.equals } : undefined,
    min: input.min, max: input.max, step: input.step,
  }));

  components.push({ type: 'validate-button', id: '__validate', dataKey: '__validate', label: 'Validate' });
  return components;
}

const steps = [
  { key: 'form', label: 'Fill Form', icon: FileText },
  { key: 'review', label: 'Review', icon: Shield },
  { key: 'submitted', label: 'Submitted', icon: Send },
];

export function RequestPage() {
  const { slug } = useParams<{ slug: string }>();
  const navigate = useNavigate();
  const {
    context, setContext, updateFormData, setSidebarOpen, addMessage,
    threadId, getHistoryForAgent, replaceLoading, messages,
  } = useConversationStore();

  const [detail, setDetail] = useState<CatalogDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [validated, setValidated] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  const values = context.formData || {};
  const currentStep = context.step || 'form';

  useEffect(() => {
    if (!slug) return;

    setContext({ catalogSlug: slug, step: 'form' });
    setSidebarOpen(true);

    api.getCatalogItem(slug)
      .then((data) => {
        setDetail(data);
        if (!context.formData || Object.keys(context.formData).length === 0) {
          const defaults: Record<string, unknown> = {};
          for (const input of data.inputs) {
            if (input.default !== null && input.default !== undefined) {
              defaults[input.id] = input.default;
            }
          }
          setContext({ formData: defaults });
        }

        const hasGreeted = messages.some(
          (m) => m.role === 'assistant' && m.text.includes(data.item.name) && m.text.includes('form')
        );
        if (!hasGreeted) {
          sendToAgent(`User opened the ${data.item.name} request form. Briefly greet and offer to help fill it out.`);
        }
      })
      .catch((err) => setError(err.message))
      .finally(() => setLoading(false));
  }, [slug]);

  useEffect(() => {
    const lastMsg = messages[messages.length - 1];
    if (lastMsg?.role === 'assistant' && lastMsg.fieldSuggestions) {
      setValidated(false);
    }
  }, [messages]);

  const sendToAgent = async (message: string, action?: string) => {
    addMessage({ role: 'assistant', text: '', isLoading: true });
    try {
      const res = await fetch(buildAgentUrl('/catalog/chat'), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          threadId,
          message,
          catalogSlug: slug,
          formData: context.formData,
          history: getHistoryForAgent(),
          action: action || undefined,
        }),
      });
      const data = await res.json();
      replaceLoading({ role: 'assistant', text: data.reply || '' });

      if (data.fieldSuggestions) {
        setContext({
          formData: { ...(context.formData || {}), ...data.fieldSuggestions },
        });
        setValidated(false);
      }

      if (data.validationResults?.results) {
        const newErrors: Record<string, string> = {};
        for (const r of data.validationResults.results) {
          if (!r.passed && r.message) newErrors[r.fieldId] = r.message;
        }
        setErrors(newErrors);
        if (data.validationResults.isValid) {
          setValidated(true);
          setContext({ step: 'review' });
        }
      }
    } catch {
      replaceLoading({ role: 'assistant', text: 'Failed to reach assistant.' });
    }
  };

  const handleChange = (key: string, value: unknown) => {
    updateFormData(key, value);
    setErrors((prev) => { const n = { ...prev }; delete n[key]; return n; });
    setValidated(false);
  };

  const handleValidate = () => {
    if (!detail || !slug) return;
    addMessage({ role: 'user', text: 'Please validate my form.' });
    sendToAgent('User clicked Validate. Check all fields and report any issues.', 'validate');
  };

  const handleSubmit = async () => {
    if (!detail || submitting) return;
    setSubmitting(true);
    try {
      const data = await api.createRequest({ catalogItemId: detail.item.id, inputs: values });
      await api.submitRequest(data.id);
      addMessage({ role: 'assistant', text: 'Request submitted successfully!' });
      setContext({ step: 'submitted' });
      setTimeout(() => navigate(`/requests/${data.id}`), 1200);
    } catch {
      addMessage({ role: 'assistant', text: 'Failed to submit. Please try again.' });
    } finally {
      setSubmitting(false);
    }
  };

  if (loading) {
    return (
      <div className="max-w-3xl mx-auto space-y-4">
        <div className="skeleton h-8 w-48" />
        <div className="skeleton h-4 w-80" />
        <div className="skeleton h-96" />
      </div>
    );
  }

  if (error || !detail) {
    return (
      <div className="flex flex-col items-center justify-center h-64 gap-2">
        <p className="text-[14px] font-medium" style={{ color: 'var(--danger)' }}>{error || 'Service not found'}</p>
        <Link to="/catalog" className="text-[13px] font-medium" style={{ color: 'var(--accent)' }}>Back to catalog</Link>
      </div>
    );
  }

  const a2uiComponents = mapInputsToA2UI(detail.inputs);

  return (
    <div className="max-w-3xl mx-auto space-y-6">
      {/* Breadcrumb */}
      <Link
        to="/catalog"
        className="inline-flex items-center gap-1.5 text-[12px] font-medium transition-colors hover:text-[var(--accent)]"
        style={{ color: 'var(--text-muted)' }}
      >
        <ArrowLeft size={14} /> Back to catalog
      </Link>

      {/* Header */}
      <div>
        <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
          {detail.item.name}
        </h1>
        <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
          {detail.item.description}
        </p>
      </div>

      {/* Progress stepper */}
      <div className="flex items-center gap-0">
        {steps.map((step, i) => {
          const isActive = step.key === currentStep;
          const isPast = steps.findIndex(s => s.key === currentStep) > i;
          const StepIcon = step.icon;

          return (
            <div key={step.key} className="flex items-center flex-1">
              <div className="flex items-center gap-2">
                <div
                  className="w-7 h-7 rounded-full flex items-center justify-center text-[11px] font-bold shrink-0"
                  style={{
                    backgroundColor: isActive ? 'var(--accent)' : isPast ? 'var(--success)' : 'var(--bg-secondary)',
                    color: isActive || isPast ? 'white' : 'var(--text-muted)',
                    border: !isActive && !isPast ? '1px solid var(--border-color)' : undefined,
                  }}
                >
                  {isPast ? <CheckCircle size={14} /> : <StepIcon size={13} />}
                </div>
                <span
                  className="text-[12px] font-medium hidden sm:block"
                  style={{ color: isActive ? 'var(--accent)' : isPast ? 'var(--success)' : 'var(--text-muted)' }}
                >
                  {step.label}
                </span>
              </div>
              {i < steps.length - 1 && (
                <div
                  className="flex-1 h-px mx-3"
                  style={{ backgroundColor: isPast ? 'var(--success)' : 'var(--border-color)' }}
                />
              )}
            </div>
          );
        })}
      </div>

      {/* Form */}
      <div
        className="rounded-xl border p-6"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
      >
        <A2UIRenderer
          components={a2uiComponents}
          values={values}
          errors={errors}
          onChange={handleChange}
          onValidate={handleValidate}
        />

        {validated && (
          <div className="mt-6 pt-4" style={{ borderTop: '1px solid var(--border-color)' }}>
            <button
              onClick={handleSubmit}
              disabled={submitting}
              className="w-full py-3 text-[13px] font-semibold rounded-lg transition-all flex items-center justify-center gap-2"
              style={{
                backgroundColor: 'var(--success)',
                color: 'white',
                opacity: submitting ? 0.7 : 1,
              }}
            >
              {submitting ? (
                <><Loader2 size={16} className="animate-spin" /> Submitting...</>
              ) : (
                <><CheckCircle size={16} /> Submit Request</>
              )}
            </button>
          </div>
        )}
      </div>

      {/* Hint */}
      {!useConversationStore.getState().sidebarOpen && (
        <p className="text-center text-[12px]" style={{ color: 'var(--text-muted)' }}>
          Press <kbd className="px-1.5 py-0.5 rounded text-[11px] font-mono" style={{ backgroundColor: 'var(--bg-secondary)', border: '1px solid var(--border-color)' }}>⌘K</kbd> to
          ask the AI assistant for help filling this form
        </p>
      )}
    </div>
  );
}
