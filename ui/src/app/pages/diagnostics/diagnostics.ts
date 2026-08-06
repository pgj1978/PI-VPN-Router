import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';

import { Api } from '../../core/api';
import { CheckStatus, Diagnostics } from '../../core/api.models';
import { RouterStore, describe } from '../../core/router-store';

@Component({
  selector: 'app-diagnostics',
  imports: [MatCardModule, MatIconModule, MatButtonModule, MatProgressBarModule],
  templateUrl: './diagnostics.html',
  styleUrl: './diagnostics.scss',
})
export class DiagnosticsPage implements OnInit {
  private readonly api = inject(Api);
  private readonly store = inject(RouterStore);

  protected readonly report = signal<Diagnostics | null>(null);
  protected readonly running = signal(false);

  protected readonly summary = computed(() => {
    const checks = this.report()?.checks ?? [];
    return {
      pass: checks.filter((c) => c.status === 'pass').length,
      warn: checks.filter((c) => c.status === 'warn').length,
      fail: checks.filter((c) => c.status === 'fail').length,
    };
  });

  ngOnInit(): void {
    this.run();
  }

  protected run(): void {
    this.running.set(true);
    this.api.diagnostics().subscribe({
      next: (result) => {
        this.report.set(result);
        this.running.set(false);
      },
      error: (err) => {
        this.running.set(false);
        this.store.notify(describe(err), 'error');
      },
    });
  }

  protected icon(status: CheckStatus): string {
    return status === 'pass' ? 'check_circle' : status === 'warn' ? 'warning' : 'error';
  }

  protected headline(overall: CheckStatus | undefined): string {
    switch (overall) {
      case 'pass': return 'Everything looks healthy';
      case 'warn': return 'Working, with warnings';
      case 'fail': return 'Something needs attention';
      default: return 'Running checks';
    }
  }
}
