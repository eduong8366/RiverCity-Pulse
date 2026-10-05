import { TestBed } from '@angular/core/testing';
import { App } from './app';

describe('App', () => {
  it('shows the product name and the source note', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector('h1')?.textContent).toBe('RiverCity Pulse');
    expect(page.querySelector('footer')?.textContent).toContain(
      'not affiliated with or endorsed by the City of Sacramento',
    );
  });
});
