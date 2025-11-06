import { useState } from 'react';
import { Card, CardContent } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { ChevronDown, ChevronUp, Copy, AlertCircle } from 'lucide-react';

interface ExceptionViewerProps {
  exception: string;
  previewLines?: number;
  variant?: 'card' | 'alert';
}

export function ExceptionViewer({
  exception,
  previewLines = 3,
  variant = 'alert'
}: ExceptionViewerProps) {
  const [isExpanded, setIsExpanded] = useState(false);
  const [copied, setCopied] = useState(false);

  const lines = exception.split('\n');
  const previewText = lines.slice(0, previewLines).join('\n');
  const hasMore = lines.length > previewLines;

  const handleCopy = () => {
    navigator.clipboard.writeText(exception);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  const handleToggle = () => {
    setIsExpanded(!isExpanded);
  };

  const ExceptionContent = () => (
    <div className="space-y-3">
      <div className="flex items-start justify-between gap-4">
        <div className="flex items-center gap-2">
          <AlertCircle className="h-4 w-4 flex-shrink-0 mt-0.5" />
          <p className="font-medium text-sm">Task Exception</p>
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="ghost"
            size="sm"
            onClick={handleCopy}
            className="h-7 text-xs"
          >
            <Copy className={copied ? 'h-3 w-3 mr-1.5 text-green-600' : 'h-3 w-3 mr-1.5'} />
            {copied ? 'Copied!' : 'Copy'}
          </Button>
          {hasMore && (
            <Button
              variant="ghost"
              size="sm"
              onClick={handleToggle}
              className="h-7 text-xs"
            >
              {isExpanded ? (
                <>
                  <ChevronUp className="h-3 w-3 mr-1.5" />
                  Collapse
                </>
              ) : (
                <>
                  <ChevronDown className="h-3 w-3 mr-1.5" />
                  Expand
                </>
              )}
            </Button>
          )}
        </div>
      </div>

      {/* Exception Text */}
      <div className="relative">
        <pre className="text-xs font-mono whitespace-pre-wrap break-words bg-gray-50 dark:bg-gray-900 p-3 rounded-md border overflow-x-auto">
          {isExpanded ? exception : previewText}
        </pre>

        {!isExpanded && hasMore && (
          <div className="absolute bottom-0 left-0 right-0 h-12 bg-gradient-to-t from-gray-50 dark:from-gray-900 to-transparent rounded-b-md" />
        )}
      </div>

      {!isExpanded && hasMore && (
        <p className="text-xs text-muted-foreground text-center">
          Showing {previewLines} of {lines.length} lines
        </p>
      )}
    </div>
  );

  if (variant === 'card') {
    return (
      <Card className="border-red-200 bg-red-50 dark:bg-red-950 dark:border-red-800">
        <CardContent className="pt-6">
          <ExceptionContent />
        </CardContent>
      </Card>
    );
  }

  return (
    <Alert variant="destructive" className="relative">
      <AlertDescription>
        <ExceptionContent />
      </AlertDescription>
    </Alert>
  );
}
