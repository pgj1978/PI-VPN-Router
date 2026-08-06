import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatListModule } from '@angular/material/list';
import { MatTooltipModule } from '@angular/material/tooltip';

import { Api } from '../../core/api';
import { DomainBypass } from '../../core/api.models';
import { RouterStore, describe } from '../../core/router-store';

@Component({
  selector: 'app-domains',
  imports: [
    FormsModule, MatCardModule, MatListModule, MatIconModule, MatButtonModule,
    MatFormFieldModule, MatInputModule, MatChipsModule, MatTooltipModule,
  ],
  templateUrl: './domains.html',
  styleUrl: './domains.scss',
})
export class DomainsPage implements OnInit {
  private readonly api = inject(Api);
  protected readonly store = inject(RouterStore);

  protected readonly newDomain = signal('');
  protected readonly working = signal(false);

  ngOnInit(): void {
    this.store.refreshDomains();
  }

  protected add(): void {
    const domain = this.newDomain().trim().toLowerCase();
    if (!domain) return;

    this.working.set(true);
    this.api.addDomain(domain).subscribe({
      next: (result) => {
        this.working.set(false);
        this.store.domains.set(result.domains);
        this.newDomain.set('');
        this.store.notify(`${domain} will now bypass the VPN`);
      },
      error: (err) => {
        this.working.set(false);
        this.store.notify(describe(err), 'error');
      },
    });
  }

  protected remove(entry: DomainBypass): void {
    this.api.removeDomain(entry.domain).subscribe({
      next: (result) => {
        this.store.domains.set(result.domains);
        this.store.notify(`${entry.domain} now goes through the VPN`);
      },
      error: (err) => this.store.notify(describe(err), 'error'),
    });
  }

  /** Re-resolves every domain now. Addresses drift, and stale rules silently stop working. */
  protected refresh(): void {
    this.working.set(true);
    this.api.refreshDomains().subscribe({
      next: (result) => {
        this.working.set(false);
        this.store.domains.set(result.domains);
        this.store.notify('Re-resolved all bypass domains');
      },
      error: (err) => {
        this.working.set(false);
        this.store.notify(describe(err), 'error');
      },
    });
  }
}
