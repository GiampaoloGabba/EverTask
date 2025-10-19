import { Loader2 } from 'lucide-react';
import { cn } from '@/lib/utils';

interface LoadingSpinnerProps {
  text?: string;
  className?: string;
  size?: 'sm' | 'md' | 'lg';
}

export function LoadingSpinner({ text, className, size = 'md' }: LoadingSpinnerProps) {
  const sizeClasses = {
    sm: 'h-4 w-4',
    md: 'h-8 w-8',
    lg: 'h-12 w-12',
  };

  return (
    <div className={cn('flex flex-col items-center justify-center gap-3 py-8', className)}>
      <Loader2 className={cn(sizeClasses[size], 'animate-spin text-blue-600')} />
      {text && <p className="text-sm text-muted-foreground">{text}</p>}
    </div>
  );
}
