import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import type { Feature, FeatureCollection, Geometry } from 'geojson';
import * as L from 'leaflet';
import { MapNeighborhood, NeighborhoodBoundaries } from '../api/models';
import { fillFor, legendLabels, NO_DATA, quantileCuts, RAMP, shaded } from './bins';

type Boundary = Feature<Geometry, { NAME: string; slug: string }>;

/**
 * Leaflet choropleth of median days to close per neighborhood: five quantile bins over the neighborhoods with 30+
 * closed requests, the rest grey. Hovering (or focusing) a neighborhood emits its slug; clicking it (or Enter) picks
 * it, and the picked one stays outlined.
 */
@Component({
  selector: 'app-neighborhood-map',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './neighborhood-map.html',
  styleUrl: './neighborhood-map.scss',
})
export class NeighborhoodMapComponent {
  readonly boundaries = input<NeighborhoodBoundaries>();
  readonly neighborhoods = input<readonly MapNeighborhood[]>([]);
  readonly hovered = output<string | null>();
  /** The picked neighborhood (its drawer is open). */
  readonly selected = input<string | null>(null);
  readonly picked = output<string>();

  private readonly host = viewChild.required<ElementRef<HTMLElement>>('map');
  private readonly ready = signal(false);
  private readonly highlighted = signal<string | null>(null);
  private readonly drawn = signal(false);
  private map?: L.Map;
  private layer?: L.GeoJSON;
  private outline?: L.GeoJSON;
  private readonly features = new Map<string, Boundary>();

  private readonly bySlug = computed(
    () => new Map(this.neighborhoods().map((n) => [n.slug, n] as const)),
  );
  protected readonly cuts = computed(() =>
    quantileCuts(shaded(this.neighborhoods()).map((n) => n.medianDays as number)),
  );
  protected readonly legend = computed(() =>
    legendLabels(this.cuts()).map((label, i) => ({ label, color: RAMP[i] })),
  );
  protected readonly noData = NO_DATA;

  constructor() {
    afterNextRender(() => {
      // No basemap: the 129 shapes carry the page, and there's no tile service to depend on (or key to hold).
      this.map = L.map(this.host().nativeElement, {
        scrollWheelZoom: false,
        zoomSnap: 0.25,
      });
      this.map.attributionControl.setPrefix(
        'Boundaries: City of Sacramento · <a href="https://leafletjs.com">Leaflet</a>',
      );
      // The highlight is drawn in its own pane above the shapes rather than by bringing the shape to the front:
      // moving a focused <path> in the DOM drops its focus, and moving a hovered one can lose its mouseout.
      const pane = this.map.createPane('highlight');
      pane.style.zIndex = '450';
      pane.style.pointerEvents = 'none';
      this.ready.set(true);
    });

    // Draw the boundaries once they and the map exist.
    effect(() => {
      const geo = this.boundaries();
      if (!this.ready() || !geo || this.layer || !this.map) {
        return;
      }
      this.layer = L.geoJSON(geo as FeatureCollection, {
        style: (f) => this.style(f as Boundary),
        onEachFeature: (f, layer) => this.wire(f as Boundary, layer as L.Path),
      }).addTo(this.map);
      this.map.fitBounds(this.layer.getBounds(), { padding: [8, 8] });
      this.drawn.set(true);
    });

    // Restyle when the figures change.
    effect(() => {
      this.bySlug();
      this.cuts();
      this.layer?.setStyle((f) => this.style(f as Boundary));
    });

    // Outline the hovered neighborhood, else the picked one (once the shapes are drawn).
    effect(() => {
      this.drawn();
      const feature = this.features.get(this.highlighted() ?? this.selected() ?? '');
      this.outline?.remove();
      this.outline = undefined;
      if (feature && this.map) {
        this.outline = L.geoJSON(feature, {
          pane: 'highlight',
          interactive: false,
          style: { color: '#10242B', weight: 2.5, fill: false },
        }).addTo(this.map);
      }
    });

    inject(DestroyRef).onDestroy(() => this.map?.remove());
  }

  private style(feature: Boundary): L.PathOptions {
    return {
      fillColor: fillFor(this.bySlug().get(feature.properties.slug), this.cuts()),
      fillOpacity: 0.88,
      color: '#FFFFFF',
      weight: 0.8,
    };
  }

  private wire(feature: Boundary, layer: L.Path): void {
    const slug = feature.properties.slug;
    this.features.set(slug, feature);
    const enter = () => {
      this.highlighted.set(slug);
      this.hovered.emit(slug);
    };
    const leave = () => {
      if (this.highlighted() === slug) {
        this.highlighted.set(null);
        this.hovered.emit(null);
      }
    };
    const pick = () => this.picked.emit(slug);
    layer.on({ mouseover: enter, mouseout: leave, click: pick });
    // Keyboard: each neighborhood is a focusable shape named for screen readers.
    layer.on('add', () => {
      const element = layer.getElement();
      if (element) {
        element.setAttribute('tabindex', '0');
        element.setAttribute('role', 'button');
        element.setAttribute('aria-label', feature.properties.NAME);
        element.addEventListener('focus', enter);
        element.addEventListener('blur', leave);
        element.addEventListener('keydown', (event) => {
          const key = (event as KeyboardEvent).key;
          if (key === 'Enter' || key === ' ') {
            event.preventDefault();
            pick();
          }
        });
      }
    });
  }
}
