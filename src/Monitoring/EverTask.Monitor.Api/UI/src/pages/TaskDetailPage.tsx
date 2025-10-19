import { useParams, useNavigate } from 'react-router-dom';
import { useTaskDetail } from '@/hooks/useTasks';
import { TaskDetailModal } from '@/components/tasks/TaskDetailModal';
import { LoadingSpinner } from '@/components/common/LoadingSpinner';
import { Button } from '@/components/ui/button';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { ArrowLeft, AlertCircle } from 'lucide-react';

export function TaskDetailPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { data: task, isLoading, isError } = useTaskDetail(id || '');

  if (isLoading) {
    return <LoadingSpinner text="Loading task details..." />;
  }

  if (isError || !task) {
    return (
      <div className="space-y-4">
        <Button variant="outline" onClick={() => navigate('/tasks')}>
          <ArrowLeft className="h-4 w-4 mr-2" />
          Back to Tasks
        </Button>
        <Alert variant="destructive">
          <AlertCircle className="h-4 w-4" />
          <AlertDescription>
            Failed to load task details. The task may not exist or you may not have permission to view it.
          </AlertDescription>
        </Alert>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <Button variant="outline" onClick={() => navigate('/tasks')}>
        <ArrowLeft className="h-4 w-4 mr-2" />
        Back to Tasks
      </Button>

      <TaskDetailModal task={task} />
    </div>
  );
}
