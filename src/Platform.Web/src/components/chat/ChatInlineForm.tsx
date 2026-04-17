import { useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Loader2, CheckCircle2, AlertCircle, ArrowRight } from 'lucide-react';
import { A2UIRenderer } from '@/components/a2ui/A2UIRenderer';
import type { A2UIComponent } from '@/lib/types';
import { useConversationStore } from '@/stores/conversationStore';
import { buildAgentUrl } from '@/lib/runtimeConfig';

interface Surface {
  slug?: string;
  serviceName?: string;
  serviceDescription?: string;
  components: A2UIComponent[];
}

interface ValidationResult {
  isValid: boolean;
  results: Array<{ fieldId: string; passed: boolean; message: string }>;
}

interface Props {
  /** Raw JSON string produced by A2UIFormGenerator on the backend. */
  surfaceJson: string;
  /** Initial values to seed the form from the agent's field suggestions. */
  initialValues?: Record<string, unknown>;
}

export function ChatInlineForm({ surfaceJson, initialValues }: Props) {
  const navigate = useNavigate();
  const { threadId, setContext } = useConversationStore();

  const surface = useMemo<Surface | null>(() => {
    try {
      return JSON.parse(surfaceJson) as Surface;
    } catch {
      return null;
    }
  }, [surfaceJson]);

  const [values, setValues] = useState<Record<string, unknown>>(() => {
    const seed: Record<string, unknown> = { ...(initialValues || {}) };
    if (surface) {
      for (const c of surface.components) {
        if (c.defaultValue !== undefined && seed[c.dataKey] === undefined) {
          seed[c.dataKey] = c.defaultValue;
        }
      }
    }
    return seed;
  });
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [validating, setValidating] = useState(false);
  const [validated, setValidated] = useState(false);
  const validatingRef = useRef(false);

  if (!surface) {
    return (
      <div
        className="mt-2 mr-2 p-3 rounded-lg text-xs"
        style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}
      >
        Failed to render form.
      </div>
    );
  }

  if (!surface.slug) {
    return (
      <div
        className="mt-2 mr-2 p-3 rounded-lg text-xs flex items-center gap-1.5"
        style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}
      >
        <AlertCircle size={12} /> This form is missing a service identifier and can't be validated.
      </div>
    );
  }

  const handleChange = (key: string, value: unknown) => {
    setValues((v) => ({ ...v, [key]: value }));
    setErrors((e) => {
      if (!e[key]) return e;
      const { [key]: _, ...rest } = e;
      return rest;
    });
    setValidated(false);
  };

  const handleValidate = async () => {
    if (validatingRef.current) return;
    validatingRef.current = true;
    setValidating(true);
    try {
      const res = await fetch(buildAgentUrl('/catalog/chat'), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          threadId,
          catalogSlug: surface.slug,
          formData: values,
          action: 'validate',
        }),
      });
      if (!res.ok) {
        setErrors({ __root: `Validation request failed (HTTP ${res.status}).` });
        return;
      }
      const data = await res.json();
      const result: ValidationResult | undefined = data.validationResults;
      if (!result) {
        setErrors({ __root: 'Validation service returned no results. Please try again.' });
        return;
      }
      const newErrors: Record<string, string> = {};
      for (const r of result.results) {
        if (!r.passed) newErrors[r.fieldId] = r.message;
      }
      setErrors(newErrors);
      setValidated(result.isValid);
    } catch {
      setErrors({ __root: 'Could not reach the validation service.' });
    } finally {
      validatingRef.current = false;
      setValidating(false);
    }
  };

  const handleContinue = () => {
    setContext({ catalogSlug: surface.slug, formData: values, step: 'review' });
    navigate(`/catalog/${surface.slug}`);
  };

  return (
    <div
      className="mt-2 mr-2 p-3 rounded-lg border"
      style={{ backgroundColor: 'var(--bg-primary)', borderColor: 'var(--border-color)' }}
    >
      {surface.serviceName && (
        <div className="mb-3">
          <div className="text-sm font-semibold" style={{ color: 'var(--text-primary)' }}>
            {surface.serviceName}
          </div>
          {surface.serviceDescription && (
            <div className="text-xs mt-0.5" style={{ color: 'var(--text-muted)' }}>
              {surface.serviceDescription}
            </div>
          )}
        </div>
      )}

      <A2UIRenderer
        components={surface.components}
        values={values}
        errors={errors}
        onChange={handleChange}
        onValidate={handleValidate}
        readOnly={validating}
      />

      {errors.__root && (
        <div
          className="mt-2 text-xs flex items-center gap-1.5"
          style={{ color: 'var(--danger, #dc2626)' }}
        >
          <AlertCircle size={12} /> {errors.__root}
        </div>
      )}

      {validating && (
        <div
          className="mt-2 text-xs flex items-center gap-1.5"
          style={{ color: 'var(--text-muted)' }}
        >
          <Loader2 size={12} className="animate-spin" /> Validating…
        </div>
      )}

      {validated && !validating && (
        <div className="mt-3 flex flex-col gap-2">
          <div
            className="text-xs flex items-center gap-1.5"
            style={{ color: 'var(--success, #059669)' }}
          >
            <CheckCircle2 size={12} /> All validations passed.
          </div>
          <button
            onClick={handleContinue}
            className="self-start flex items-center gap-1.5 text-xs font-semibold px-3 py-2 rounded-lg border transition-all hover:shadow-md"
            style={{
              borderColor: 'var(--accent)',
              color: 'var(--accent)',
              backgroundColor: 'var(--bg-primary)',
            }}
          >
            Review and submit <ArrowRight size={12} />
          </button>
        </div>
      )}
    </div>
  );
}
