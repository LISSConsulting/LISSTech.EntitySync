import type { ReactNode } from 'react'
import { motion, useReducedMotion } from 'motion/react'

const paths = [
  'M-120 250C80 80 216 365 410 180S710 12 860 185s285 196 520-25 304 70 304 70',
  'M-180 410C45 210 220 494 426 286S752 98 930 275s310 152 532-70 286 68 286 68',
  'M-80 590C124 420 250 666 485 466s340-154 512 14 292 146 492-44 260 30 260 30',
  'M160 -80C265 120 82 265 286 402s432-4 532 182 82 318 350 408',
  'M1260 -90C1118 98 1302 272 1080 390S688 420 630 652 382 824 124 914',
]

const colors = ['#d75555', '#4268a9', '#788252', '#b96f4a', '#d2a34a']

export function BackgroundLines({ children }: { children: ReactNode }) {
  const reducedMotion = useReducedMotion()

  return (
    <div className="background-lines">
      <svg className="background-lines__svg" viewBox="0 0 1440 900" aria-hidden="true">
        {paths.map((path, index) => (
          <motion.path
            d={path}
            fill="none"
            key={path}
            stroke={colors[index]}
            strokeLinecap="round"
            strokeWidth="3"
            initial={reducedMotion ? { opacity: 0.26 } : { pathLength: 0.08, pathOffset: 0, opacity: 0 }}
            animate={reducedMotion ? { opacity: 0.26 } : { pathLength: [0.08, 0.5, 0.08], pathOffset: [0, 0.55, 1], opacity: [0, 0.42, 0] }}
            transition={reducedMotion ? undefined : { duration: 12 + index * 1.7, delay: index * 0.75, ease: 'linear', repeat: Infinity }}
          />
        ))}
      </svg>
      <div className="background-lines__content">{children}</div>
    </div>
  )
}
