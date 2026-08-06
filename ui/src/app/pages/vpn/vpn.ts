import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';

import { Api } from '../../core/api';
import { VpnProfile } from '../../core/api.models';
import { RouterStore, describe, formatAge, formatBytes } from '../../core/router-store';
import { ConfirmDialog, ConfirmDialogData } from '../../shared/confirm-dialog';
import { LogDialog, LogDialogData } from '../../shared/log-dialog';

@Component({
  selector: 'app-vpn',
  imports: [
    FormsModule, MatCardModule, MatButtonModule, MatIconModule, MatSlideToggleModule,
    MatFormFieldModule, MatInputModule, MatProgressBarModule, MatTooltipModule, MatDialogModule,
  ],
  templateUrl: './vpn.html',
  styleUrl: './vpn.scss',
})
export class VpnPage implements OnInit {
  private readonly api = inject(Api);
  private readonly dialog = inject(MatDialog);
  protected readonly store = inject(RouterStore);

  protected readonly formatBytes = formatBytes;
  protected readonly formatAge = formatAge;

  /** Name of the profile currently being connected, so only that row shows a spinner. */
  protected readonly connecting = signal<string | null>(null);
  protected readonly showAddForm = signal(false);
  protected readonly newName = signal('');
  protected readonly newConfig = signal('');

  ngOnInit(): void {
    this.store.refreshProfiles();
  }

  protected connect(profile: VpnProfile): void {
    this.connecting.set(profile.name);

    this.api.connect(profile.name).subscribe({
      next: (result) => {
        this.connecting.set(null);
        this.store.refreshProfiles();
        this.store.refreshStatus();

        if (result.success) {
          this.store.notify(`Connected to ${profile.name}`);
        } else {
          this.store.notify(result.error ?? 'Connection failed', 'error');
          this.showLog(`Failed to connect to ${profile.name}`, result.log, result.error);
        }
      },
      error: (err) => {
        this.connecting.set(null);
        this.store.refreshStatus();

        // A failed connect is the case where the operator most needs the raw wg-quick output,
        // so surface it rather than reducing everything to a toast.
        const body = err?.error;
        this.store.notify(describe(err), 'error');
        this.showLog(`Failed to connect to ${profile.name}`, body?.log ?? '', body?.error ?? describe(err));
      },
    });
  }

  protected disconnect(): void {
    this.connecting.set('__disconnect__');
    this.api.disconnect().subscribe({
      next: () => {
        this.connecting.set(null);
        this.store.refreshProfiles();
        this.store.refreshStatus();
        this.store.notify('VPN disconnected');
      },
      error: (err) => {
        this.connecting.set(null);
        this.store.notify(describe(err), 'error');
      },
    });
  }

  protected toggleKillSwitch(enabled: boolean): void {
    // Optimistic, then corrected by the response; the toggle must feel immediate.
    this.store.killSwitch.set(enabled);

    this.api.setKillSwitch(enabled).subscribe({
      next: (result) => {
        this.store.killSwitch.set(result.killSwitchEnabled);
        this.store.notify(`Kill switch ${result.killSwitchEnabled ? 'enabled' : 'disabled'}`);
      },
      error: (err) => {
        this.store.killSwitch.set(!enabled);
        this.store.notify(describe(err), 'error');
      },
    });
  }

  protected addProfile(): void {
    const name = this.newName().trim();
    const config = this.newConfig().trim();
    if (!name || !config) return;

    this.api.addProfile(name, config).subscribe({
      next: () => {
        this.showAddForm.set(false);
        this.newName.set('');
        this.newConfig.set('');
        this.store.refreshProfiles();
        this.store.notify(`Added profile ${name}`);
      },
      error: (err) => this.store.notify(describe(err), 'error'),
    });
  }

  protected async remove(profile: VpnProfile): Promise<void> {
    const data: ConfirmDialogData = {
      title: `Delete ${profile.name}?`,
      message: 'The WireGuard configuration file will be removed from the router. This cannot be undone.',
      confirmText: 'Delete',
      destructive: true,
    };

    const confirmed = await this.dialog.open(ConfirmDialog, { data }).afterClosed().toPromise();
    if (!confirmed) return;

    this.api.deleteProfile(profile.name).subscribe({
      next: () => {
        this.store.refreshProfiles();
        this.store.notify(`Deleted ${profile.name}`);
      },
      error: (err) => this.store.notify(describe(err), 'error'),
    });
  }

  private showLog(title: string, log: string, error?: string | null): void {
    const data: LogDialogData = { title, log, error: error ?? null };
    this.dialog.open(LogDialog, { data, width: '760px', maxWidth: '95vw' });
  }
}
