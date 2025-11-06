// Auth-related types matching backend DTOs

/**
 * Login request containing user credentials.
 */
export interface LoginRequest {
  username: string;
  password: string;
}

/**
 * Login response containing JWT token and expiration information.
 */
export interface LoginResponse {
  token: string;
  expiresAt: string; // ISO 8601 date string
  username: string;
}

/**
 * Token validation response containing validation result and token information.
 */
export interface TokenValidationResponse {
  isValid: boolean;
  username?: string;
  expiresAt?: string; // ISO 8601 date string
}
