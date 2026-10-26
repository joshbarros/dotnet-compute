import { Component, inject, signal } from '@angular/core';
import { AuthService } from '../core/auth.service';

@Component({
  selector: 'app-login',
  templateUrl: './login.component.html',
})
export class LoginComponent {
  private readonly auth = inject(AuthService);

  readonly mode = signal<'login' | 'register'>('login');
  readonly email = signal('');
  readonly password = signal('');
  readonly error = signal('');
  readonly busy = signal(false);

  toggle(): void {
    this.mode.set(this.mode() === 'login' ? 'register' : 'login');
    this.error.set('');
  }

  field(target: { set(value: string): void }, event: Event): void {
    target.set((event.target as HTMLInputElement).value);
  }

  async submit(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try {
      const action = this.mode() === 'login' ? this.auth.login : this.auth.register;
      await action(this.email().trim(), this.password());
      this.password.set('');
    } catch (e: unknown) {
      const body = e as { error?: { title?: string; detail?: string }; status?: number };
      this.error.set(
        body.error?.detail ??
          body.error?.title ??
          (body.status === 429 ? 'Too many attempts. Try again shortly.' : 'The request failed.'),
      );
    } finally {
      this.busy.set(false);
    }
  }
}
