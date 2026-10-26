import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, firstValueFrom } from 'rxjs';
import { AuthResponse, UserInfo } from './models';

/** Sent on refresh and logout so a page on another site cannot make the browser call them. */
export const REQUESTED_WITH = { 'X-Requested-With': 'compute-web' };

/**
 * The access token lives in memory only. The refresh token is an HttpOnly cookie the page cannot read,
 * so a reload signs the user back in through POST /api/auth/refresh and nothing sensitive is in storage.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly state = signal<{ token: string; user: UserInfo } | null>(null);
  private inFlight: Promise<boolean> | null = null;

  readonly user = computed(() => this.state()?.user ?? null);
  readonly isStaff = computed(() => {
    const roles = this.user()?.roles ?? [];
    return roles.includes('Admin') || roles.includes('Staff');
  });
  readonly ready = signal(false);

  token = () => this.state()?.token ?? null;

  /** Called once at startup: resumes the signed-in state when the refresh cookie is still good. */
  async restore(): Promise<void> {
    await this.refresh();
    this.ready.set(true);
  }

  login = (email: string, password: string) =>
    this.enter(this.http.post<AuthResponse>('/api/auth/login', { email, password }));

  register = (email: string, password: string) =>
    this.enter(this.http.post<AuthResponse>('/api/auth/register', { email, password }));

  /** Concurrent callers share one request: refresh tokens are single-use, so a second call would look like a replay. */
  refresh(): Promise<boolean> {
    this.inFlight ??= this.doRefresh().finally(() => (this.inFlight = null));
    return this.inFlight;
  }

  async logout(): Promise<void> {
    try {
      await firstValueFrom(this.http.post('/api/auth/logout', {}, { headers: REQUESTED_WITH }));
    } finally {
      this.state.set(null);
    }
  }

  private async doRefresh(): Promise<boolean> {
    try {
      const auth = await firstValueFrom(
        this.http.post<AuthResponse>('/api/auth/refresh', {}, { headers: REQUESTED_WITH }),
      );
      this.state.set({ token: auth.accessToken, user: auth.user });
      return true;
    } catch {
      this.state.set(null);
      return false;
    }
  }

  private async enter(request: Observable<AuthResponse>): Promise<void> {
    const auth = await firstValueFrom(request);
    this.state.set({ token: auth.accessToken, user: auth.user });
  }
}
