import { useState } from 'react';
import { Prism as SyntaxHighlighter } from 'react-syntax-highlighter';
import { vscDarkPlus } from 'react-syntax-highlighter/dist/esm/styles/prism';
import { Button } from '@/components/ui/button';
import { Copy, Check } from 'lucide-react';
import { formatJSONString, tryParseJSON } from '@/utils/formatters';
import { Card } from '@/components/ui/card';

interface JsonViewerProps {
  jsonString: string;
  className?: string;
}

export function JsonViewer({ jsonString, className }: JsonViewerProps) {
  const [copied, setCopied] = useState(false);

  const handleCopy = () => {
    navigator.clipboard.writeText(formattedJson);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  const parsed = tryParseJSON(jsonString);
  const isValidJson = typeof parsed !== 'string';
  const formattedJson = isValidJson ? formatJSONString(jsonString) : jsonString;

  if (!isValidJson) {
    return (
      <Card className={className}>
        <div className="p-4">
          <div className="flex items-center justify-between mb-2">
            <span className="text-sm font-medium text-muted-foreground">Invalid JSON</span>
          </div>
          <pre className="text-sm text-gray-700 whitespace-pre-wrap break-words">
            {jsonString}
          </pre>
        </div>
      </Card>
    );
  }

  return (
    <Card className={className}>
      <div className="relative">
        <div className="absolute top-2 right-2 z-10">
          <Button
            variant="ghost"
            size="sm"
            onClick={handleCopy}
            className="h-8 w-8 p-0 bg-gray-800 hover:bg-gray-700"
          >
            {copied ? (
              <Check className="h-4 w-4 text-green-400" />
            ) : (
              <Copy className="h-4 w-4 text-gray-300" />
            )}
          </Button>
        </div>
        <SyntaxHighlighter
          language="json"
          style={vscDarkPlus}
          customStyle={{
            margin: 0,
            borderRadius: '0.5rem',
            fontSize: '0.875rem',
            maxHeight: '600px',
          }}
        >
          {formattedJson}
        </SyntaxHighlighter>
      </div>
    </Card>
  );
}
