import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';

import { Api } from '../../core/api';
import { Device } from '../../core/api.models';
import { RouterStore, describe } from '../../core/router-store';
import { ConfirmDialog, ConfirmDialogData } from '../../shared/confirm-dialog';

@Component({
  selector: 'app-devices',
  imports: [
    FormsModule, MatCardModule, MatTableModule, MatSlideToggleModule, MatIconModule,
    MatButtonModule, MatFormFieldModule, MatInputModule, MatTooltipModule, MatMenuModule,
    MatChipsModule, MatDialogModule,
  ],
  templateUrl: './devices.html',
  styleUrl: './devices.scss',
})
export class DevicesPage implements OnInit {
  private readonly api = inject(Api);
  private readonly dialog = inject(MatDialog);
  protected readonly store = inject(RouterStore);

  protected readonly columns = ['status', 'name', 'ip', 'mac', 'reservation', 'bypass', 'actions'];
  protected readonly filter = signal('');
  protected readonly editing = signal<string | null>(null);
  protected readonly draftLabel = signal('');
  protected readonly draftIp = signal('');

  protected readonly filtered = computed(() => {
    const needle = this.filter().trim().toLowerCase();
    const devices = this.store.devices();
    if (!needle) return devices;

    return devices.filter((d) =>
      [d.label, d.hostname, d.ip, d.mac, d.staticIp]
        .some((field) => field?.toLowerCase().includes(needle)));
  });

  ngOnInit(): void {
    this.store.refreshDevices();
  }

  protected displayName(device: Device): string {
    return device.label || device.hostname || 'Unknown device';
  }

  protected toggleBypass(device: Device, bypass: boolean): void {
    // Reflect immediately, then reconcile from the authoritative response.
    this.store.devices.update((list) =>
      list.map((d) => (d.mac === device.mac ? { ...d, bypassVpn: bypass } : d)));

    this.api.setBypass(device.mac, bypass).subscribe({
      next: (result) => {
        this.store.applyDevices(result);
        this.store.notify(
          `${this.displayName(device)} now ${bypass ? 'bypasses the VPN' : 'uses the VPN'}`);
      },
      error: (err) => {
        this.store.devices.update((list) =>
          list.map((d) => (d.mac === device.mac ? { ...d, bypassVpn: !bypass } : d)));
        this.store.notify(describe(err), 'error');
      },
    });
  }

  protected startEdit(device: Device): void {
    this.editing.set(device.mac);
    this.draftLabel.set(device.label ?? '');
    this.draftIp.set(device.staticIp ?? '');
  }

  protected cancelEdit(): void {
    this.editing.set(null);
  }

  protected saveEdit(device: Device): void {
    const label = this.draftLabel().trim() || null;
    const ip = this.draftIp().trim() || null;

    this.api.setLabel(device.mac, label).subscribe({
      next: () => {
        this.api.setStaticIp(device.mac, ip).subscribe({
          next: (result) => {
            this.store.applyDevices(result);
            this.editing.set(null);
            this.store.notify('Device updated');
          },
          error: (err) => this.store.notify(describe(err), 'error'),
        });
      },
      error: (err) => this.store.notify(describe(err), 'error'),
    });
  }

  protected async forget(device: Device): Promise<void> {
    const data: ConfirmDialogData = {
      title: `Forget ${this.displayName(device)}?`,
      message: 'Its bypass setting, label and DHCP reservation will be removed. '
        + 'The device can still connect and will get a fresh address.',
      confirmText: 'Forget',
      destructive: true,
    };

    const confirmed = await this.dialog.open(ConfirmDialog, { data }).afterClosed().toPromise();
    if (!confirmed) return;

    this.api.forgetDevice(device.mac).subscribe({
      next: (result) => {
        this.store.applyDevices(result);
        this.store.notify('Device forgotten');
      },
      error: (err) => this.store.notify(describe(err), 'error'),
    });
  }
}
