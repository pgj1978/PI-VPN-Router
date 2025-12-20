import { Component, Output, EventEmitter } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink, RouterLinkActive } from '@angular/router';

@Component({
  selector: 'app-sidebar',
  standalone: true,
  imports: [CommonModule, RouterLink, RouterLinkActive],
  templateUrl: './sidebar.component.html',
  styleUrl: './sidebar.component.css'
})
export class SidebarComponent {
  menuOpen = true;
  @Output() toggle = new EventEmitter<boolean>();

  toggleMenu() {
    this.menuOpen = !this.menuOpen;
    this.toggle.emit(this.menuOpen);
  }
}
