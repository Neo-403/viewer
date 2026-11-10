import { useCallback, useEffect, useMemo, useState } from 'react'
import { api, type CameraDto, type LayoutDto } from '../api'
import { useI18n } from '../i18n'
import { Icon } from '../components/Icon'
import { useAuth } from '../auth'
import { EmptyCell, LiveTile } from '../components/LiveTile'
import { StillTile } from '../components/StillTile'
import { destroyIdle } from '../live/liveSession'
import { ConfirmModal, TextPromptModal } from '../components/Modals'

// Grid size is the side N of an NxN grid (matches the backend/desktop model);
// cells = N*N. The UI labels each option by its cell count (1, 4, 9, 16, 25).
const SIDES = [1, 2, 3, 4, 5]

// Preset refresh rates for stills mode, same ladder as the desktop grid.
// The interval box is editable, so these are shortcuts, not a hard limit.
const STILL_INTERVALS = [2, 5, 10, 30, 60]
const MIN_STILL_INTERVAL = 1
const MAX_STILL_INTERVAL = 3600
// How long a kiosk wall holds a page before flipping to the next one.
const ROTATE_INTERVALS = [10, 30, 60]

function clampStillInterval(n: number): number {
  if (!Number.isFinite(n)) return 5
  return Math.min(MAX_STILL_INTERVAL, Math.max(MIN_STILL_INTERVAL, Math.round(n)))
}

// Phase offset for one camera in a staggered wall: spread N grabs over ONE
// interval, keeping every camera's own period exactly `intervalSeconds`.
// Serializing grabs instead ("wait for the previous one to finish") would make
// a round take sum(grab durations) — at 2 s / 9 cameras that is already over
// 2 s, so the wall would silently refresh slower than requested. Requests may
// overlap when a grab lasts longer than its slot, but nothing ever queues.
// Mirrors StillsSchedule.PhaseMs in the .NET Core project.
export function stillsPhaseMs(index: number, count: number, intervalSeconds: number, stagger: boolean): number {
  if (!stagger || count <= 1 || index <= 0) return 0
  return (intervalSeconds * 1000 * Math.min(index, count - 1)) / count
}

// Wall preferences (stills, kiosk rotation) are per viewer, not per install:
// the same layout is watched on a control-room screen and on someone's laptop,
// and only one of them wants a full-screen wall of stills. So they live in the
// browser, unlike layouts, which are shared server state.
function loadPref<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem('openipc.grid.' + key)
    return raw === null ? fallback : (JSON.parse(raw) as T)
  } catch {
    return fallback
  }
}
function savePref(key: string, value: unknown) {
  try {
    localStorage.setItem('openipc.grid.' + key, JSON.stringify(value))
  } catch { /* private mode / quota — the preference just won't stick */ }
}


// The Live grid, driven by saved layouts (name + grid size + ordered camera
// tiles). Switching between layouts keeps shared cameras playing. An edit mode
// assigns a camera to each cell and changes the grid size.
//
// A layout may hold more cameras than the grid has cells (the desktop's 82-camera
// "Default" is one), so the tile list is paginated exactly like the desktop head:
// one page = gridSize² cells, page count = ceil(tiles / pageSize). Only the
// current page gets live tiles — flipping pages tears the old sockets down.
export function Grid() {
  const { t } = useI18n()
  const { user, can } = useAuth()
  const [cameras, setCameras] = useState<CameraDto[] | null>(null)
  const [layouts, setLayouts] = useState<LayoutDto[] | null>(null)
  const [activeId, setActiveId] = useState<number | null>(null)
  const [page, setPage] = useState(0)
  const [editing, setEditing] = useState(false)
  const [draftSize, setDraftSize] = useState(4)
  const [draftTiles, setDraftTiles] = useState<(string | null)[]>([])
  const [busy, setBusy] = useState(false)
  // Which dialog is open, if any (replaces native prompt/confirm).
  const [dialog, setDialog] = useState<'create' | 'rename' | 'delete' | null>(null)
  // Wall modes: periodic stills instead of live streams, and a chrome-free
  // fullscreen wall that can cycle through the layout's pages on its own.
  const [stills, setStills] = useState(() => loadPref('stills', false))
  const [stillInterval, setStillInterval] = useState(() => clampStillInterval(loadPref('stillInterval', 5)))
  // Editable buffer for the interval box: keystrokes only change this string,
  // so typing "30" doesn't restart every tile for the intermediate "3".
  const [stillIntervalText, setStillIntervalText] = useState(() => String(stillInterval))
  // Off (default) = all stills refresh together. On = phases are spread over
  // one interval to flatten the bandwidth spike; per-camera period is intact.
  const [stagger, setStagger] = useState(() => loadPref('stagger', false))
  const [rotate, setRotate] = useState(() => loadPref('rotate', false))
  const [rotateInterval, setRotateInterval] = useState(() => loadPref('rotateInterval', 30))
  const [kiosk, setKiosk] = useState(false)
  const [stage, setStage] = useState<HTMLDivElement | null>(null)

  const camById = useMemo(() => {
    const m = new Map<string, CameraDto>()
    cameras?.forEach((c) => m.set(c.id, c))
    return m
  }, [cameras])

  useEffect(() => {
    let cancelled = false
    void Promise.all([api.cameras(), api.layouts()]).then(([cs, ls]) => {
      if (cancelled) return
      setCameras(cs)
      ls.sort((a, b) => a.sortOrder - b.sortOrder)
      setLayouts(ls)
      setActiveId(ls[0]?.id ?? null)
    })
    return () => {
      cancelled = true
    }
  }, [])

  useEffect(() => {
    savePref('stills', stills)
    // LiveTile unmount only hands its pooled session back; the pool would keep
    // the <video> (and the server's ffmpeg) streaming for another 30 s. Stills
    // mode has no live viewer, so release them now. Defer one tick so the
    // child tiles' cleanup (release) has run and the sessions are idle.
    if (stills) window.setTimeout(destroyIdle, 0)
  }, [stills])
  useEffect(() => savePref('stillInterval', stillInterval), [stillInterval])
  useEffect(() => savePref('stagger', stagger), [stagger])
  // Follow preset picks / clamped commits. `stillInterval` does NOT change on
  // keystrokes, so this never overwrites what the user is in the middle of.
  useEffect(() => setStillIntervalText(String(stillInterval)), [stillInterval])
  useEffect(() => savePref('rotate', rotate), [rotate])
  useEffect(() => savePref('rotateInterval', rotateInterval), [rotateInterval])

  // Commit a typed interval on blur / Enter. Accepts any whole number of
  // seconds (1..3600), clamps out-of-range values, restores the last valid one
  // for junk. A preset chosen from the datalist applies immediately in onChange.
  const commitStillInterval = () => {
    const raw = stillIntervalText.trim()
    const n = /^\d+$/.test(raw) ? Number(raw) : NaN
    if (Number.isFinite(n)) {
      const next = clampStillInterval(n)
      if (next !== stillInterval) setStillInterval(next)
      else setStillIntervalText(String(next))
    } else {
      setStillIntervalText(String(stillInterval))
    }
  }

  // Kiosk is a CSS mode first and a fullscreen request second. The Fullscreen
  // API is refused outright in an embedded/iframed page, and a wall shown that
  // way (a kiosk browser, a dashboard frame) still wants the chrome gone — so
  // the mode never depends on the request being granted.
  const enterKiosk = () => {
    setKiosk(true)
    // Refusal comes back either way depending on the browser — a rejected
    // promise, or a synchronous throw that would take the click handler (and
    // the mode with it) down. Both mean the same thing: CSS-only kiosk.
    try {
      void stage?.requestFullscreen?.()?.catch(() => { /* CSS-only kiosk, then */ })
    } catch { /* CSS-only kiosk, then */ }
  }
  const exitKiosk = useCallback(() => {
    setKiosk(false)
    if (document.fullscreenElement) void document.exitFullscreen()
  }, [])

  // Leaving fullscreen by the browser's own means (Esc, F11, the tile that went
  // fullscreen on its own) must drop the mode too, and Esc has to work in the
  // CSS-only case where the browser has no fullscreen to leave.
  useEffect(() => {
    if (!kiosk) return
    const onFullscreenChange = () => {
      if (!document.fullscreenElement) setKiosk(false)
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !document.fullscreenElement) setKiosk(false)
    }
    document.addEventListener('fullscreenchange', onFullscreenChange)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('fullscreenchange', onFullscreenChange)
      document.removeEventListener('keydown', onKey)
    }
  }, [kiosk])

  const active = layouts?.find((l) => l.id === activeId) ?? null

  // A layout is global, but a restricted user may only see part of it. Drop the
  // tiles they can't see before paginating — otherwise someone with two cameras
  // pages through ten screens of empty cells.
  const restricted = user?.cameras != null
  const tiles = active
    ? restricted
      ? active.tiles.filter((id) => camById.has(id))
      : active.tiles
    : []

  // Pagination of the active layout. Clamped rather than corrected in state, so
  // a layout that shrank under us (another client edited it) still renders.
  const pageSize = active ? active.gridSize * active.gridSize : 1
  const pageCount = active ? Math.max(1, Math.ceil(tiles.length / pageSize)) : 1
  const safePage = Math.min(page, pageCount - 1)
  const pageStart = safePage * pageSize

  // Auto-flip through the pages of a kiosk wall. Only in kiosk: someone working
  // in the console wants the page they chose to stay put.
  useEffect(() => {
    if (!kiosk || !rotate || pageCount < 2 || editing) return
    const timer = window.setInterval(
      () => setPage((p) => (p + 1) % pageCount),
      Math.max(5, rotateInterval) * 1000,
    )
    return () => window.clearInterval(timer)
  }, [kiosk, rotate, rotateInterval, pageCount, editing])

  async function reloadLayouts(): Promise<LayoutDto[]> {
    const ls = await api.layouts()
    ls.sort((a, b) => a.sortOrder - b.sortOrder)
    setLayouts(ls)
    setActiveId((prev) => (prev != null && ls.some((l) => l.id === prev) ? prev : (ls[0]?.id ?? null)))
    return ls
  }

  // ---- edit-mode actions --------------------------------------------------
  const startEdit = () => {
    if (!active) return
    setDraftSize(active.gridSize)
    setDraftTiles(Array.from({ length: pageSize }, (_, i) => active.tiles[pageStart + i] ?? null))
    setEditing(true)
  }
  const changeDraftSize = (n: number) => {
    setDraftSize(n)
    setDraftTiles((prev) => Array.from({ length: n * n }, (_, i) => prev[i] ?? null))
  }
  const setCell = (i: number, camId: string) =>
    setDraftTiles((prev) => {
      const next = [...prev]
      next[i] = camId || null
      return next
    })
  const save = async () => {
    if (!active) return
    setBusy(true)
    try {
      if (draftSize !== active.gridSize) await api.updateLayout(active.id, { gridSize: draftSize })
      // The editor shows one page; everything outside it must survive untouched,
      // so splice the edited cells back between the pages before and after.
      // Tiles are a compact ordered list (no holes), so emptied cells are dropped
      // and the tail slides up — that's the model, not a bug.
      const edited = draftTiles.filter((x): x is string => !!x)
      const head = active.tiles.slice(0, pageStart)
      const tail = active.tiles.slice(pageStart + pageSize)
      await api.setLayoutTiles(active.id, [...head, ...edited, ...tail])
      await reloadLayouts()
      setEditing(false)
      // A different grid size re-cuts every page boundary; the old page index
      // would land somewhere arbitrary, so go back to the first page.
      if (draftSize !== active.gridSize) setPage(0)
    } finally {
      setBusy(false)
    }
  }

  const createLayout = async (name: string) => {
    setDialog(null)
    const created = await api.createLayout(name, 2)
    await reloadLayouts()
    setActiveId(created.id)
    setPage(0)
  }
  const renameLayout = async (name: string) => {
    setDialog(null)
    if (!active || name === active.name) return
    await api.updateLayout(active.id, { name })
    await reloadLayouts()
  }
  const deleteLayout = async () => {
    setDialog(null)
    if (!active) return
    await api.deleteLayout(active.id)
    setEditing(false)
    setActiveId(null)
    setPage(0)
    await reloadLayouts()
  }

  const loading = cameras === null || layouts === null

  // A still never needs more pixels than its cell. 1×1 fills the window, so
  // keep it sharp; denser grids ask the camera for a much smaller JPEG.
  // Mirrors MajesticSnapshotOptions.ForGridSize in the .NET Core project.
  const stillMaxWidth = active?.gridSize === 1 ? 1920 : active?.gridSize === 2 ? 960 : 640

  // Stagger phase is per *visible camera*: empty cells issue no request and
  // must not consume a slot in the interval.
  const pageCells = Array.from({ length: pageSize }, (_, i) => camById.get(tiles[pageStart + i] ?? ''))
  const stillCount = pageCells.filter(Boolean).length
  let stillIndex = 0
  const stillIndexes = pageCells.map((c) => {
    if (!c) return -1
    const at = stillIndex
    stillIndex += 1
    return at
  })

  return (
    <div className="wrap" style={{ maxWidth: 'none' }}>
      <div className="toolbar" style={{ flexWrap: 'wrap' }}>
        <h1 style={{ margin: 0 }}>{t('Grid.Title')}</h1>
        <div className="tabs" style={{ flex: 1 }}>
          {layouts?.map((l) => (
            <button
              key={l.id}
              className={l.id === activeId ? 'active' : ''}
              onClick={() => {
                setEditing(false)
                setActiveId(l.id)
                setPage(0)
              }}
            >
              {l.name}
            </button>
          ))}
          {!editing && can('Manage') && !restricted && (
            <button className="row" onClick={() => setDialog('create')}>
              <Icon name="plus" size={13} /> {t('Grid.NewLayout')}
            </button>
          )}
        </div>
        {active && !editing && (
          <div className="row">
            <button
              className={stills ? 'active' : ''}
              title={t('Grid.StillsHint')}
              onClick={() => setStills(!stills)}
            >
              {t('Grid.Stills')}
            </button>
            {stills && (
              <>
                <label className="row" title={t('Grid.StillsIntervalHint')}>
                  <input
                    type="text"
                    inputMode="numeric"
                    list="still-interval-presets"
                    value={stillIntervalText}
                    style={{ width: 62 }}
                    onChange={(e) => {
                      const raw = e.target.value
                      setStillIntervalText(raw)
                      // A preset picked from the datalist applies at once;
                      // typed values wait for blur / Enter.
                      const preset = Number(raw)
                      if (STILL_INTERVALS.includes(preset)) setStillInterval(preset)
                    }}
                    onBlur={commitStillInterval}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') {
                        commitStillInterval()
                        e.currentTarget.blur()
                      }
                    }}
                  />
                  <datalist id="still-interval-presets">
                    {STILL_INTERVALS.map((s) => <option key={s} value={s} />)}
                  </datalist>
                  <span className="muted">s</span>
                </label>
                <label className="row" title={t('Grid.StillsStaggerHint')}>
                  <input
                    type="checkbox"
                    checked={stagger}
                    onChange={(e) => setStagger(e.target.checked)}
                  />
                  {t('Grid.StillsStagger')}
                </label>
              </>
            )}
            <button className="row" title={t('Grid.KioskHint')} onClick={enterKiosk}>
              <Icon name="maximize" size={13} /> {t('Grid.Kiosk')}
            </button>
          </div>
        )}
        {active && !editing && can('Manage') && !restricted && (
          <button onClick={startEdit}>{t('Grid.Edit')}</button>
        )}
        {editing && (
          <div className="row">
            <button onClick={() => setDialog('rename')}>{t('Grid.Rename')}</button>
            <button className="danger" onClick={() => setDialog('delete')}>
              {t('Grid.DeleteLayout')}
            </button>
            <span style={{ width: 8 }} />
            <button onClick={() => setEditing(false)}>{t('Common.Cancel')}</button>
            <button className="primary" onClick={save} disabled={busy}>
              {t('Common.Save')}
            </button>
          </div>
        )}
      </div>

      {editing && (
        <div className="tabs" style={{ marginBottom: 8 }}>
          <span className="muted" style={{ marginRight: 8 }}>
            {t('Grid.Size')}:
          </span>
          {SIDES.map((n) => (
            <button key={n} className={draftSize === n ? 'active' : ''} onClick={() => changeDraftSize(n)}>
              {n * n}
            </button>
          ))}
        </div>
      )}

      {loading ? (
        <div className="spin">{t('Common.Loading')}</div>
      ) : !active ? (
        <p className="muted">{t('Grid.NoLayouts')}</p>
      ) : editing ? (
        <>
        {pageCount > 1 && (
          <p className="muted" style={{ margin: '0 0 8px', fontSize: 12 }}>
            {t('Grid.EditingPage')} {safePage + 1} / {pageCount}
          </p>
        )}
        <div className={`videos n${draftSize * draftSize}`}>
          {draftTiles.map((camId, i) => (
            <div className="cell empty-slot" key={i} style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 12 }}>
              <select value={camId ?? ''} onChange={(e) => setCell(i, e.target.value)} style={{ maxWidth: '90%' }}>
                <option value="">{t('Grid.EmptyCell')}</option>
                {cameras!.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.name}
                  </option>
                ))}
              </select>
            </div>
          ))}
        </div>
        </>
      ) : (
        <div className={'stage' + (kiosk ? ' kiosk' : '')} ref={setStage}>
          {kiosk && (
            <div className="kiosk-bar">
              {pageCount > 1 && (
                <>
                  <label className="row">
                    <input type="checkbox" checked={rotate} onChange={(e) => setRotate(e.target.checked)} />
                    {t('Grid.Rotate')}
                  </label>
                  {rotate && (
                    <select value={rotateInterval} onChange={(e) => setRotateInterval(Number(e.target.value))}>
                      {ROTATE_INTERVALS.map((s) => (
                        <option key={s} value={s}>
                          {s} {t('Common.Seconds')}
                        </option>
                      ))}
                    </select>
                  )}
                </>
              )}
              <button onClick={exitKiosk}>{t('Grid.KioskExit')}</button>
            </div>
          )}
          <div className={`videos n${pageSize}`}>
            {Array.from({ length: pageSize }, (_, i) => {
              const cam = pageCells[i]
              if (!cam) return <EmptyCell key={`empty-${i}`} />
              return stills ? (
                <StillTile
                  key={cam.id}
                  camera={cam}
                  intervalSeconds={stillInterval}
                  phaseMs={stillsPhaseMs(stillIndexes[i], stillCount, stillInterval, stagger)}
                  maxWidth={stillMaxWidth}
                />
              ) : (
                <LiveTile key={cam.id} camera={cam} />
              )
            })}
          </div>
          {pageCount > 1 && (
            <div className="pager">
              <button disabled={safePage === 0} title={t('Grid.PrevPage')} onClick={() => setPage(safePage - 1)}>
                <Icon name="chevronLeft" size={14} />
              </button>
              {Array.from({ length: pageCount }, (_, i) => (
                <button
                  key={i}
                  className={i === safePage ? 'active' : ''}
                  onClick={() => setPage(i)}
                >
                  {i + 1}
                </button>
              ))}
              <button
                disabled={safePage + 1 >= pageCount}
                title={t('Grid.NextPage')}
                onClick={() => setPage(safePage + 1)}
              >
                <Icon name="chevronRight" size={14} />
              </button>
            </div>
          )}
        </div>
      )}

      {dialog === 'create' && (
        <TextPromptModal
          title={t('Grid.NewLayoutTitle')}
          label={t('Grid.NewLayoutName')}
          submitLabel={t('Common.Save')}
          onSubmit={createLayout}
          onCancel={() => setDialog(null)}
        />
      )}
      {dialog === 'rename' && active && (
        <TextPromptModal
          title={t('Grid.Rename')}
          label={t('Grid.NewLayoutName')}
          initial={active.name}
          submitLabel={t('Common.Save')}
          onSubmit={renameLayout}
          onCancel={() => setDialog(null)}
        />
      )}
      {dialog === 'delete' && active && (
        <ConfirmModal
          title={t('Grid.DeleteLayout')}
          message={t('Grid.DeleteLayoutConfirm')}
          confirmLabel={t('Cameras.Delete')}
          danger
          onConfirm={deleteLayout}
          onCancel={() => setDialog(null)}
        />
      )}
    </div>
  )
}
