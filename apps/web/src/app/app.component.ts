import { Component, inject } from '@angular/core';
import { AuthService } from './core/auth.service';
import { LoginComponent } from './login/login.component';
import { WorkspaceComponent } from './workspace/workspace.component';

@Component({
  selector: 'app-root',
  imports: [LoginComponent, WorkspaceComponent],
  templateUrl: './app.component.html',
})
export class AppComponent {
  readonly auth = inject(AuthService);

  signOut(): Promise<void> {
    return this.auth.logout();
  }
}
