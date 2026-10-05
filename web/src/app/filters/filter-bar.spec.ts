import { provideLocationMocks } from '@angular/common/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FilterBarComponent } from './filter-bar';
import { FilterStore } from './filter-store';

describe('FilterBarComponent', () => {
  let fixture: ComponentFixture<FilterBarComponent>;
  let store: FilterStore;
  let page: HTMLElement;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideLocationMocks()] });
    fixture = TestBed.createComponent(FilterBarComponent);
    fixture.componentRef.setInput('categories', ['Parking', 'Streets']);
    store = TestBed.inject(FilterStore);
    page = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  function windowButtons(): HTMLButtonElement[] {
    return Array.from(page.querySelectorAll('button'));
  }

  it('marks the current window as pressed', () => {
    expect(
      windowButtons().map((b) => [b.textContent?.trim(), b.getAttribute('aria-pressed')]),
    ).toEqual([
      ['30 days', 'false'],
      ['90 days', 'true'],
      ['365 days', 'false'],
    ]);
  });

  it('sets the window, category and district', async () => {
    windowButtons()[2].click();
    const [category, district] = Array.from(page.querySelectorAll('select'));
    category.value = 'Streets';
    category.dispatchEvent(new Event('change'));
    district.value = '4';
    district.dispatchEvent(new Event('change'));
    await fixture.whenStable();

    expect(store.filters()).toEqual({ window: 365, category: 'Streets', district: 4 });
    expect(windowButtons()[2].getAttribute('aria-pressed')).toBe('true');

    category.value = '';
    category.dispatchEvent(new Event('change'));
    expect(store.category()).toBeNull();
  });

  it('keeps a category from the URL that is not in the list', async () => {
    store.category.set('Measure O');
    await fixture.whenStable();

    const select = page.querySelector('select') as HTMLSelectElement;
    expect(select.value).toBe('Measure O');
  });
});
