/**
 * 表情通道归属（design D15/D19 的延伸）。
 *
 * 有的 VRM 在 VRM.blendShapeGroups 里带真实 binds（默认那份只有名称、binds 为空）。
 * 这类模型的 three-vrm 表情管理器会在每帧 update() 里把自己的权重（默认 0）写回
 * 同一批 Fcl_* 形变，把 FaceDriver 的直写盖掉——症状是表情与口型偶发失灵，
 * 而且看起来像随机的（要等管理器更新一次才归零）。
 *
 * 我们只保留一条写入通道：把管理器的表达式全部注销。binds 仍留在材质上，
 * 只是没有人再往那些下标写权重，FaceDriver 成为唯一写入者。
 */
export interface ExpressionManagerLike {
  readonly expressions: readonly unknown[];
  unregisterExpression(expression: unknown): void;
}

/** 注销管理器里的所有表达式，返回注销个数（null/undefined 时返回 0）。 */
export function detachExpressions(manager: ExpressionManagerLike | null | undefined): number {
  if (!manager) return 0;
  const registered = [...manager.expressions];
  for (const expression of registered) {
    manager.unregisterExpression(expression);
  }
  return registered.length;
}
