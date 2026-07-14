/*
 * EqCurveView.kt – Draggable 3-band EQ curve (Bass / Mid / Treble).
 */
package net.quietrebellion

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
import android.graphics.PointF
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View
import com.google.android.material.color.MaterialColors
import kotlin.math.abs
import kotlin.math.roundToInt
import kotlin.math.sqrt

class EqCurveView @JvmOverloads constructor(
    ctx: Context, attrs: AttributeSet? = null,
) : View(ctx, attrs) {

    private val values = IntArray(3) { 0 }
    private val mins   = IntArray(3) { -10 }
    private val maxs   = IntArray(3) { 10 }

    var onBandChanged: ((bandId: Int, value: Int) -> Unit)? = null
    var onBandReleased: ((bandId: Int, value: Int) -> Unit)? = null

    fun setBand(bandId: Int, value: Int, min: Int, max: Int) {
        values[bandId] = value.coerceIn(min, max)
        mins[bandId]   = min
        maxs[bandId]   = max
        invalidate()
    }

    // ── Paints ────────────────────────────────────────────────────────────────

    private val dp = ctx.resources.displayMetrics.density

    private val curveColor = MaterialColors.getColor(ctx, com.google.android.material.R.attr.colorOnSurface, Color.BLACK)
    private val fanColor   = MaterialColors.getColor(ctx, com.google.android.material.R.attr.colorOutlineVariant, Color.LTGRAY)
    private val dotColor   = MaterialColors.getColor(ctx, android.R.attr.colorPrimary, Color.BLUE)

    private val curvePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = curveColor; strokeWidth = 4f * dp; style = Paint.Style.STROKE
        strokeCap = Paint.Cap.ROUND; strokeJoin = Paint.Join.ROUND
    }
    private val zeroLinePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = curveColor; strokeWidth = 1.5f * dp; style = Paint.Style.STROKE; alpha = 35
    }
    // ponytail: one line per dB step; ceiling = 20 lines max (range ±10)
    private val fanPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = fanColor; strokeWidth = 1f * dp; style = Paint.Style.STROKE
    }
    private val handleFillPaint   = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = dotColor; style = Paint.Style.FILL }
    private val handleStrokePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = dotColor; strokeWidth = 1.5f * dp; style = Paint.Style.STROKE
    }

    // ── Geometry ──────────────────────────────────────────────────────────────

    private val PAD = 36f
    private val R   = 22f
    private val TR  = 52f
    private var drag = -1

    private fun xAt(i: Int) = width * (i + 1) / 4f

    private fun yAt(i: Int): Float {
        val norm = (values[i] - mins[i]).toFloat() / (maxs[i] - mins[i])
        return (height - PAD) - norm * (height - 2 * PAD)
    }

    private fun yZeroFor(i: Int): Float {
        val norm = (0 - mins[i]).toFloat() / (maxs[i] - mins[i])
        return (height - PAD) - norm * (height - 2 * PAD)
    }

    private fun valAt(i: Int, y: Float): Int {
        val norm = 1f - (y - PAD) / (height - 2 * PAD)
        return (mins[i] + norm * (maxs[i] - mins[i])).roundToInt().coerceIn(mins[i], maxs[i])
    }

    // ── Drawing ───────────────────────────────────────────────────────────────

    override fun onDraw(canvas: Canvas) {
        val w = width.toFloat()
        val p = Array(3) { PointF(xAt(it), yAt(it)) }

        // Zero baseline
        canvas.drawLine(0f, yZeroFor(0), w, yZeroFor(0), zeroLinePaint)

        // Fan lines per band – one per dB step, only where value ≠ 0
        // bass → [0, xAt(0)], mid → [xAt(0), xAt(2)], treble → [xAt(2), w]
        val zones = arrayOf(0f to xAt(0), xAt(0) to xAt(2), xAt(2) to w)
        for (i in 0..2) {
            val v = values[i]; if (v == 0) continue
            val steps = abs(v)
            val yz = yZeroFor(i)
            val (xL, xR) = zones[i]
            for (j in 1..steps) {
                val yLine = yz + j.toFloat() / steps * (p[i].y - yz)
                canvas.drawLine(xL, yLine, xR, yLine, fanPaint)
            }
        }

        // EQ curve (Catmull-Rom)
        val tx = floatArrayOf(p[1].x - p[0].x, (p[2].x - p[0].x) / 2f, p[2].x - p[1].x)
        val ty = floatArrayOf(p[1].y - p[0].y, (p[2].y - p[0].y) / 2f, p[2].y - p[1].y)
        val path = Path().apply {
            moveTo(p[0].x, p[0].y)
            for (seg in 0..1) cubicTo(
                p[seg].x   + tx[seg]   / 3f, p[seg].y   + ty[seg]   / 3f,
                p[seg+1].x - tx[seg+1] / 3f, p[seg+1].y - ty[seg+1] / 3f,
                p[seg+1].x, p[seg+1].y,
            )
        }
        canvas.drawPath(path, curvePaint)

        // Primary-color handles
        for (pt in p) {
            canvas.drawCircle(pt.x, pt.y, R, handleFillPaint)
            canvas.drawCircle(pt.x, pt.y, R, handleStrokePaint)
        }
    }

    // ── Touch ─────────────────────────────────────────────────────────────────

    override fun onTouchEvent(e: MotionEvent): Boolean {
        when (e.action) {
            MotionEvent.ACTION_DOWN -> {
                var best = TR; drag = -1
                for (i in 0..2) {
                    val dx = e.x - xAt(i); val dy = e.y - yAt(i)
                    val d = sqrt(dx * dx + dy * dy)
                    if (d < best) { best = d; drag = i }
                }
                if (drag >= 0) parent.requestDisallowInterceptTouchEvent(true)
            }
            MotionEvent.ACTION_MOVE -> if (drag >= 0) {
                values[drag] = valAt(drag, e.y)
                onBandChanged?.invoke(drag, values[drag])
                invalidate()
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> if (drag >= 0) {
                onBandReleased?.invoke(drag, values[drag])
                parent.requestDisallowInterceptTouchEvent(false)
                drag = -1
            }
        }
        return drag >= 0 || e.action == MotionEvent.ACTION_DOWN
    }
}
