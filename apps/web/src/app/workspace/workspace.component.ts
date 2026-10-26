import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { Reservation, ReservationStatus, LogEntry, Pool } from '../core/models';

const BADGES: Record<ReservationStatus, string> = {
  Requested: 'bg-slate-100 text-slate-700 ring-slate-300',
  Held: 'bg-amber-50 text-amber-800 ring-amber-300',
  Leased: 'bg-emerald-50 text-emerald-800 ring-emerald-300',
  Cancelled: 'bg-sky-50 text-sky-800 ring-sky-300',
  Rejected: 'bg-rose-50 text-rose-800 ring-rose-300',
};

@Component({
  selector: 'app-workspace',
  imports: [DatePipe],
  templateUrl: './workspace.component.html',
})
export class WorkspaceComponent implements OnInit {
  private readonly api = inject(ApiService);
  readonly auth = inject(AuthService);
  private readonly destroyRef = inject(DestroyRef);

  readonly pools = signal<Pool[]>([]);
  readonly selectedCode = signal('');
  readonly reservations = signal<Reservation[]>([]);
  readonly log = signal<LogEntry[]>([]);
  readonly logFor = signal<string | null>(null);
  readonly error = signal('');

  readonly code = signal('');
  readonly name = signal('H100 Pool East');
  readonly gpuModel = signal('H100');
  readonly region = signal('us-east1');
  readonly capacity = signal('12');

  readonly reference = signal('');
  readonly hours = signal('1');

  readonly selected = computed(() => this.pools().find((s) => s.code === this.selectedCode()));

  ngOnInit(): void {
    void this.refresh();
    const timer = setInterval(() => void this.refresh(), 1000);
    this.destroyRef.onDestroy(() => clearInterval(timer));
  }

  badge(status: ReservationStatus): string {
    return BADGES[status];
  }

  money(cents: number): string {
    return (cents / 100).toLocaleString('en-US', { style: 'currency', currency: 'USD' });
  }

  async refresh(): Promise<void> {
    try {
      const pools = await this.api.pools();
      this.pools.set(pools);
      if (!this.selectedCode() && pools.length > 0) this.selectedCode.set(pools[0].code);
      if (this.selectedCode())
        this.reservations.set(await this.api.reservations(this.selectedCode()));
      const id = this.logFor();
      if (id && this.auth.isStaff()) this.log.set(await this.api.events(id));
    } catch (e: unknown) {
      // A 401 means the session is gone; the interceptor has already signed the user out.
      if ((e as { status?: number }).status !== 401) this.error.set('The API is not reachable.');
      return;
    }
    if (this.error() === 'The API is not reachable.') this.error.set('');
  }

  async select(code: string): Promise<void> {
    this.selectedCode.set(code);
    this.logFor.set(null);
    this.log.set([]);
    await this.refresh();
  }

  async openPool(): Promise<void> {
    await this.run(async () => {
      const code = this.code();
      const pool = await this.api.openPool({
        code,
        name: this.name(),
        gpuModel: this.gpuModel(),
        region: this.region(),
        hourCapacity: Number(this.capacity()),
      });
      if (this.code() === code) this.code.set('');
      this.selectedCode.set(pool.code);
    });
  }

  async book(): Promise<void> {
    await this.run(async () => {
      const reference = this.reference();
      await this.api.book({
        poolCode: this.selectedCode(),
        reference,
        hours: Number(this.hours()),
      });
      if (this.reference() === reference) this.reference.set('');
    });
  }

  async cancel(reservation: Reservation): Promise<void> {
    await this.run(() => this.api.cancel(reservation.id));
  }

  async showLog(reservation: Reservation): Promise<void> {
    this.logFor.set(reservation.id);
    this.log.set([]);
    await this.refresh();
  }

  canCancel(reservation: Reservation): boolean {
    return reservation.status === 'Held' || reservation.status === 'Leased';
  }

  private async run(action: () => Promise<unknown>): Promise<void> {
    this.error.set('');
    try {
      await action();
    } catch (e: unknown) {
      const body = (e as { error?: { title?: string } }).error;
      this.error.set(body?.title ?? 'The request failed.');
    }
    await this.refresh();
  }

  field(signalRef: { set(value: string): void }, event: Event): void {
    signalRef.set((event.target as HTMLInputElement).value);
  }
}
