// Must match backend DTOs (camelCase JSON): RateLimitsDto / RateLimitKeyDto.
// The keyed rate limiter view is in-memory and SINGLE-NODE: it reflects this process only.

export interface RateLimitKeyDto {
  queueName: string;
  key: string;
  parkedCount: number;
  nextSlotUtc: string;
}

export interface RateLimitsDto {
  // False when no rate-limiter introspection is available (e.g. standalone API mode):
  // all other values are zero/empty.
  enabled: boolean;
  throttledTasks: number;
  maxParkedTasks: number;
  trackedKeys: number;
  failOpenCount: number;
  keys: RateLimitKeyDto[];
}
