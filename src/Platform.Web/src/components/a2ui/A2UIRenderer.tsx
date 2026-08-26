import type { A2UIComponent } from '@/lib/types';
import { TextInput } from './TextInput';
import { Select } from './Select';
import { Toggle } from './Toggle';
import { NumberInput } from './NumberInput';
import { KeyValueList } from './KeyValueList';
import { InfoBanner } from './InfoBanner';
import { ValidateButton } from './ValidateButton';
import { ReviewCard } from './ReviewCard';
import { MultiSelect } from './MultiSelect';
import { SecretField } from './SecretField';
import { CodeBlock } from './CodeBlock';
import { ResourcePicker } from './ResourcePicker';
import { UserPicker } from './UserPicker';
import { EnvironmentSelector } from './EnvironmentSelector';
import { FileUpload } from './FileUpload';
import { TextArea } from './TextArea';

interface Props {
  components: A2UIComponent[];
  values: Record<string, unknown>;
  errors: Record<string, string>;
  onChange: (key: string, value: unknown) => void;
  onValidate: () => void;
  readOnly?: boolean;
}

const renderers: Record<string, React.ComponentType<ComponentProps>> = {
  'text-input': TextInput,
  'select': Select,
  'toggle': Toggle,
  'number-input': NumberInput,
  'multi-select': MultiSelect,
  'secret-field': SecretField,
  'code-block': CodeBlock,
  'key-value-list': KeyValueList,
  'resource-picker': ResourcePicker,
  'user-picker': UserPicker,
  'environment-selector': EnvironmentSelector,
  'file-upload': FileUpload,
  'text-area': TextArea,
  'info-banner': InfoBanner as unknown as React.ComponentType<ComponentProps>,
  'validate-button': ValidateButton as unknown as React.ComponentType<ComponentProps>,
  'review-card': ReviewCard as unknown as React.ComponentType<ComponentProps>,
};

export interface ComponentProps {
  component: A2UIComponent;
  value: unknown;
  error?: string;
  onChange: (value: unknown) => void;
  readOnly?: boolean;
  /** All sibling form values — used by dynamic-source components like ResourcePicker. */
  allValues?: Record<string, unknown>;
}

export function A2UIRenderer({ components, values, errors, onChange, onValidate, readOnly }: Props) {
  return (
    <div className="space-y-5">
      {components.map((comp) => {
        // Visibility condition
        if (comp.visibleWhen) {
          const depValue = values[comp.visibleWhen.field];
          if (depValue !== comp.visibleWhen.equals) return null;
        }

        const Renderer = renderers[comp.type];
        if (!Renderer) {
          return (
            <div key={comp.id} className="text-sm" style={{ color: 'var(--text-muted)' }}>
              Unknown component type: {comp.type}
            </div>
          );
        }

        if (comp.type === 'validate-button') {
          return <ValidateButton key={comp.id} onValidate={onValidate} />;
        }

        return (
          <Renderer
            key={comp.id}
            component={comp}
            value={values[comp.dataKey]}
            error={errors[comp.dataKey]}
            onChange={(v) => onChange(comp.dataKey, v)}
            readOnly={readOnly}
            allValues={values}
          />
        );
      })}
    </div>
  );
}
