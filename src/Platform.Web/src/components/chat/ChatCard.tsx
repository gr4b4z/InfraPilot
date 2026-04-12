import type { AgentCard } from '@/lib/types';
import { DeploymentListCard } from './DeploymentListCard';
import { DeploymentStateCard } from './DeploymentStateCard';
import { DeploymentActivityCard } from './DeploymentActivityCard';
import { RequestDetailCard } from './RequestDetailCard';
import { SummaryCard } from './SummaryCard';
import { TimelineCard } from './TimelineCard';

interface Props {
  card: AgentCard;
}

export function ChatCard({ card }: Props) {
  switch (card.type) {
    case 'deployment-list':
      return <DeploymentListCard title={card.title} data={card.data} />;
    case 'deployment-state':
      return <DeploymentStateCard title={card.title} data={card.data} />;
    case 'deployment-activity':
      return <DeploymentActivityCard title={card.title} data={card.data} />;
    case 'request-detail':
      return <RequestDetailCard title={card.title} data={card.data} />;
    case 'summary':
      return <SummaryCard title={card.title} data={card.data} />;
    case 'timeline':
      return <TimelineCard title={card.title} data={card.data} />;
    default:
      return null;
  }
}
