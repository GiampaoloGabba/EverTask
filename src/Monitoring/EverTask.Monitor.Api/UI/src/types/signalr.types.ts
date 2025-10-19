export interface EverTaskEventData {
  taskId: string;
  eventDateUtc: string;
  severity: 'Information' | 'Warning' | 'Error';
  taskType: string;
  taskHandlerType: string;
  taskParameters: string; // JSON
  message: string;
  exception: string | null;
}
