import { useParams } from 'react-router-dom';
import { useTaskDetail } from '@/hooks/useTasks';
import { TaskDetailModal } from '@/components/tasks/TaskDetailModal';
import { LoadingSpinner } from '@/components/common/LoadingSpinner';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { AlertCircle } from 'lucide-react';

export function TaskDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { data: task, isLoading, isError } = useTaskDetail(id || '');

  if (isLoading) {
    return <LoadingSpinner text="Loading task details..." />;
  }

  if (isError || !task) {
    return (
      <Alert variant="destructive">
        <AlertCircle className="h-4 w-4" />
        <AlertDescription>
          Failed to load task details. The task may not exist or you may not have permission to view it.
        </AlertDescription>
      </Alert>
    );
  }

  return <TaskDetailModal task={task} />;
}
