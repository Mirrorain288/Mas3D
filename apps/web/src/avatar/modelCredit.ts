/**
 * 页脚署名的唯一来源。
 *
 * 许可条款是 credit=necessary：署名必须写对作者。作者写在模型文件自身的
 * VRM meta 里（VRM 0.x: title/author，VRM 1.0: name/authors），所以换模型时
 * 由这里派生文案，而不是在界面里写死某个名字——写死换了模型就会说谎。
 */
export interface VrmMetaLike {
  readonly metaVersion?: unknown;
  readonly title?: unknown;
  readonly author?: unknown;
  readonly name?: unknown;
  readonly authors?: unknown;
}

const SITE = "hub.vroid.com";
const NON_COMMERCIAL = "非商用";

function text(value: unknown): string {
  return typeof value === "string" ? value.trim() : "";
}

function authorOf(meta: VrmMetaLike): string {
  const author = text(meta.author);
  if (author) return author;
  const authors = meta.authors;
  if (Array.isArray(authors)) {
    const joined = authors.map(text).filter((item) => item.length > 0).join(", ");
    if (joined) return joined;
  }
  return "";
}

/** 由 VRM meta 生成页脚署名；meta 缺失时给出不指认任何作者的兜底文案。 */
export function creditFromMeta(meta: VrmMetaLike | null | undefined): string {
  const title = meta ? text(meta.title) || text(meta.name) : "";
  const author = meta ? authorOf(meta) : "";
  const head = author ? `${title || "该模型"} model by ${author}` : `${title || "该模型"} · 未声明作者`;
  return `${head} · ${SITE} · ${NON_COMMERCIAL}`;
}
