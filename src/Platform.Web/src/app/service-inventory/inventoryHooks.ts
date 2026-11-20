// PROTOTYPE — hooks and formatters shared by the service inventory views.
import { useLocation } from 'react-router-dom';
import { formatDistance } from 'date-fns';
import { PROTOTYPE_NOW, readScenario, type Scenario } from './inventoryData';

export function useScenario(): Scenario {
  return readScenario(useLocation().search);
}

export function ago(iso: string): string {
  return formatDistance(new Date(iso), PROTOTYPE_NOW, { addSuffix: true });
}

