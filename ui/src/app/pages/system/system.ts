import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';

import { Api } from '../../core/api';
import { Dhcp, RulePreview } from '../../core/api.models';
import { RouterStore, describe } from '../../core/router-store';
import { ConfirmDialog, ConfirmDialogData } from '../../shared/confirm-dialog';

@Component({
  selector: 'app-system',
  imports: [
    FormsModule, MatCardModule, MatButtonModule, MatIconModule, MatFormFieldModule,
    MatInputModule, MatSlideToggleModule, MatExpansionModule, MatTooltipModule, MatDialogModule,
  ],
  templateUrl: './system.html',
  styleUrl: './system.scss',
})
export class SystemPage implements OnInit {
  private readonly api = inject(Api);
  private readonly dialog = inject(MatDialog);
  protected readonly store = inject(RouterStore);

  protected readonly dhcp = signal<Dhcp | null>(null);
  protected readonly rules = signal<RulePreview | null>(null);
  protected readonly saving = signal(false);

  protected readonly enabled = signal(true);
  protected readonly rangeStart = signal('');
  protected readonly rangeEnd = signal('');
  protected readonly leaseTime = signal('12h');

  ngOnInit(): void {
    this.store.refreshSystemInfo();
    this.loadDhcp();
    this.loadRules();
  }

  protected loadDhcp(): void {
    this.api.dhcp().subscribe({
      next: (result) => {
        this.dhcp.set(result);
        this.enabled.set(result.enabled);
        this.rangeStart.set(result.rangeStart);
        this.rangeEnd.set(result.rangeEnd);
        this.leaseTime.set(result.leaseTime);
      },
      error: (err) => this.store.notify(describe(err), 'error'),
    });
  }

  protected loadRules(): void {
    this.api.rules().subscribe({
      next: (result) => this.rules.set(result),
      error: (err) => this.store.notify(describe(err), 'error'),
    });
  }

  protected saveDhcp(): void {
    this.saving.set(true);
    this.api.setDhcp({
      enabled: this.enabled(),
      rangeStart: this.rangeStart(),
      rangeEnd: this.rangeEnd(),
      leaseTime: this.leaseTime(),
    }).subscribe({
      next: (result) => {
        this.saving.set(false);
        this.dhcp.set(result);
        this.store.notify('DHCP settings saved');
      },
      error: (err) => {
        this.saving.set(false);
        this.store.notify(describe(err), 'error');
      },
    });
  }

  protected reconcile(): void {
    this.api.reconcile().subscribe({
      next: (result) => {
        this.rules.set(result);
        this.store.notify('Firewall rules reapplied');
      },
      error: (err) => this.store.notify(describe(err), 'error'),
    });
  }

  protected async reboot(): Promise<void> {
    const data: ConfirmDialogData = {
      title: 'Reboot the router?',
      message: 'Every device on the LAN loses its connection for about a minute. '
        + 'The VPN reconnects automatically once the router is back.',
      confirmText: 'Reboot',
      destructive: true,
    };

    const confirmed = await this.dialog.open(ConfirmDialog, { data }).afterClosed().toPromise();
    if (!confirmed) return;

    this.api.reboot().subscribe({
      next: () => this.store.notify('Rebooting. The page will reconnect shortly.'),
      // A reboot tears down the connection mid-response, so a transport error here is
      // the expected outcome rather than a failure worth alarming anyone about.
      error: () => this.store.notify('Reboot requested. The page will reconnect shortly.'),
    });
  }
}
