export interface Pool {
  id: string;
  code: string;
  name: string;
  gpuModel: string;
  region: string;
  day: string;
  hourCapacity: number;
  hoursLeft: number;
}

export type ReservationStatus = 'Requested' | 'Held' | 'Leased' | 'Cancelled' | 'Rejected';

export interface Reservation {
  id: string;
  poolCode: string;
  reference: string;
  hours: number;
  feeCents: number;
  status: ReservationStatus;
  reason: string | null;
  leaseNumber: string | null;
  createdAt: string;
  updatedAt: string;
  hoursReleasedAt: string | null;
}

export interface LogEntry {
  id: string;
  type: string;
  module: string;
  occurredAt: string;
}
