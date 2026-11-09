import { memo, useEffect, useRef, useState } from 'react'
import { api, type CameraDto } from '../api'
import { useI18n } from '../i18n'

// A tile that shows a periodically refreshed still instead of a live stream —
// the web counterpart of the desktop grid's "stills" mode.
//
// The point is cost: a 25-cell wall means 25 ffmpeg sessions and 25 sockets,
// while a still costs one HTTP GET per camera per interval and the camera
// itself encodes the JPEG (Majestic /image.jpg). For a wall you glance at,
// a frame every few seconds is what you actually need.
//
// Fetched as a blob rather than pointed at with <img src>, for the same reason
// as SnapshotModal: the endpoint answers 502 when the camera doesn't respond,
// and an <img> would only show a broken icon. It also means a failed refresh
// leaves the previous frame on screen instead of blanking the tile.
export const StillTile = memo(function StillTile({ camera, intervalSeconds, phaseMs }: {
  camera: CameraDto
  intervalSeconds: number
  // Start offset inside one interval when the wall is staggered. 0 = the
  // original "all tiles refresh together" behaviour.
  phaseMs: number
}) {
  const { t } = useI18n()
  const [url, setUrl] = useState<string | null>(null)
  const [takenAt, setTakenAt] = useState<Date | null>(null)
  const [failed, setFailed] = useState(false)
  // Blob URLs live OUTSIDE the refresh effect: changing the interval or the
  // stagger phase re-runs that effect, and revoking the displayed URL in its
  // cleanup would blank every tile until the next frame arrived.
  const shownRef = useRef<string | null>(null)
  const staleRef = useRef<string | null>(null)

  // Only an actual unmount releases the frame currently on screen.
  useEffect(() => () => {
    if (shownRef.current) URL.revokeObjectURL(shownRef.current)
    if (staleRef.current) URL.revokeObjectURL(staleRef.current)
    shownRef.current = null
    staleRef.current = null
  }, [])

  useEffect(() => {
    let cancelled = false
    let timer: number | undefined
    let controller: AbortController | null = null
    // Wall-clock (ms) of the next scheduled grab. Fixed origins keep the
    // chosen interval exact and stop tiles with different latencies from
    // drifting back into one burst (which would undo the stagger).
    let nextAt = performance.now() + Math.max(0, phaseMs)

    const tick = async () => {
      // Release the generation BEFORE the one on screen (the browser stopped
      // referencing it an interval ago), never the displayed one.
      if (staleRef.current) {
        URL.revokeObjectURL(staleRef.current)
        staleRef.current = null
      }
      controller = new AbortController()
      try {
        const res = await fetch(api.snapshotUrl(camera.id), {
          credentials: 'same-origin',
          cache: 'no-store',
          // A slow camera must not deliver a late frame into an unmounted (or
          // re-intervalled) tile, which looked like an extra unrequested
          // refresh on top of the chosen interval.
          signal: controller.signal,
        })
        if (!res.ok) throw new Error(String(res.status))
        const blob = await res.blob()
        if (cancelled) return
        const next = URL.createObjectURL(blob)
        staleRef.current = shownRef.current
        shownRef.current = next
        setUrl(next)
        setTakenAt(new Date())
        setFailed(false)
      } catch {
        if (!cancelled) setFailed(true)
      }
      if (cancelled) return
      // The next grab is due one interval after THIS one was SCHEDULED, not
      // after it finished. A slow camera therefore cannot stretch the
      // interval (or a staggered round) beyond what the user picked — the
      // "wait for the previous one" problem at 2 s intervals.
      // Chained timeouts rather than setInterval: no request queue builds up.
      nextAt += Math.max(1, intervalSeconds) * 1000
      const now = performance.now()
      // Overran a whole slot: restart the cadence from now with a breather,
      // rather than firing back-to-back requests at a wedged camera.
      if (nextAt <= now) nextAt = now + 100
      timer = window.setTimeout(() => void tick(), Math.max(0, nextAt - now))
    }

    timer = window.setTimeout(() => void tick(), Math.max(0, phaseMs))
    return () => {
      cancelled = true
      controller?.abort()
      window.clearTimeout(timer)
      // Keep shownRef/staleRef alive: the next effect run reuses the frame
      // that's already on screen instead of flashing an empty tile.
    }
  }, [camera.id, intervalSeconds, phaseMs])

  return (
    <div className="cell">
      {url ? <img className="still" src={url} alt={camera.name} /> : null}
      {!url && !failed && (
        <div className="loading">
          <span className="spinner" />
        </div>
      )}
      <span className="status">
        <span className={'dot ' + (failed ? 'error' : url ? 'live' : 'connecting')} />
        {failed ? t('Grid.StillFailed') : takenAt ? takenAt.toLocaleTimeString() : t('Snapshot.Taking')}
      </span>
      <span className="label">{camera.name}</span>
    </div>
  )
})
