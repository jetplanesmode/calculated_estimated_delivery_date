-- Persist API audit trail on each calculate: rule names, working-day / holiday trace, and skip count.
-- Run in Supabase SQL editor or psql against the project database.
--
-- New databases: columns are already defined in 001_Initial_Creations.sql — use this script only to
-- ALTER an older public.edd_logs table that was created before applied_rules / non_delivery_days_skipped.

ALTER TABLE public.edd_logs
  ADD COLUMN IF NOT EXISTS applied_rules jsonb NOT NULL DEFAULT '[]'::jsonb,
  ADD COLUMN IF NOT EXISTS non_delivery_days_skipped integer NOT NULL DEFAULT 0;

COMMENT ON COLUMN public.edd_logs.applied_rules IS
  'Ordered list of applied rule names and non-working-day messages (matches API appliedRules).';
COMMENT ON COLUMN public.edd_logs.non_delivery_days_skipped IS
  'Count of non-working calendar days crossed (matches API nonDeliveryDaysSkipped).';
