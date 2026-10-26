import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Reservation, LogEntry, Pool } from './models';

export interface NewPool {
  code: string;
  name: string;
  gpuModel: string;
  region: string;
  hourCapacity: number;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  pools = () => firstValueFrom(this.http.get<Pool[]>('/api/pools'));
  openPool = (body: NewPool) => firstValueFrom(this.http.post<Pool>('/api/pools', body));

  reservations = (poolCode: string) =>
    firstValueFrom(this.http.get<Reservation[]>('/api/reservations', { params: { poolCode } }));
  book = (body: { poolCode: string; reference: string; hours: number }) =>
    firstValueFrom(this.http.post<Reservation>('/api/reservations', body));
  cancel = (id: string) =>
    firstValueFrom(this.http.post<Reservation>(`/api/reservations/${id}/cancellation`, {}));
  events = (id: string) =>
    firstValueFrom(this.http.get<LogEntry[]>(`/api/reservations/${id}/events`));
}
